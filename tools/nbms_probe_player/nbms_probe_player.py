#!/usr/bin/env python3
"""NBMS 0.1 probe player.

Loads a header, chart, and ZIP-based audio archive, then verifies references and
prints a timing schedule. This is a Phase 3 validation player, not a game UI.
"""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import tempfile
import sys
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_audio import Pcm16Audio, decode_flac_pcm16, read_wav_pcm16, write_wav_pcm16  # noqa: E402
from nbms_crypto import (  # noqa: E402
    b64d,
    chacha20_poly1305_decrypt,
    derive_passphrase_key,
    load_json as load_crypto_json,
    verify_header_signature,
)


@dataclass(frozen=True)
class ScheduledEvent:
    tick: int
    time_ms: float
    kind: str
    detail: str
    audio_id: str | None = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Probe-load an NBMS 0.1 package.")
    parser.add_argument("header", type=Path, help="Path to song.nbmh")
    parser.add_argument("--chart", help="Chart id to load. Defaults to first chart.")
    parser.add_argument("--events", action="store_true", help="Print full event schedule.")
    parser.add_argument("--realtime", action="store_true", help="Print events in realtime according to chart timing.")
    parser.add_argument("--render-wav", type=Path, help="Render scheduled FLAC audio to a WAV file.")
    parser.add_argument("--play", action="store_true", help="Render scheduled FLAC audio and play it with the OS WAV player.")
    parser.add_argument("--passphrase", help="Passphrase for encrypted .nbma payloads.")
    parser.add_argument("--public-key", type=Path, help="Public key JSON for signed headers.")
    parser.add_argument("--speed", type=float, default=1.0, help="Realtime speed multiplier.")
    parser.add_argument("--strict-codec", action="store_true", help="Fail when audio entries are not FLAC.")
    return parser.parse_args()


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def sha256_bytes(data: bytes) -> str:
    return "sha256-" + hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return "sha256-" + digest.hexdigest()


def canonical_json_hash(value: Any) -> str:
    def canonical(obj: Any) -> Any:
        if isinstance(obj, dict):
            return {key: canonical(obj[key]) for key in sorted(obj)}
        if isinstance(obj, list):
            return [canonical(item) for item in obj]
        return obj

    encoded = json.dumps(canonical(value), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return "sha256-" + hashlib.sha256(encoded).hexdigest()


def choose_chart(header: dict[str, Any], chart_id: str | None) -> dict[str, Any]:
    charts = header.get("charts", [])
    if not charts:
        raise ValueError("header has no charts")
    if chart_id is None:
        return charts[0]
    for chart in charts:
        if chart.get("id") == chart_id:
            return chart
    raise ValueError(f"chart id not found: {chart_id}")


def collect_audio_ids(chart: dict[str, Any]) -> set[str]:
    audio_ids: set[str] = set()
    for note in chart.get("notes", []):
        audio_id = note.get("audioId")
        if audio_id:
            audio_ids.add(audio_id)
    for event in chart.get("backgroundAudio", []):
        audio_id = event.get("audioId")
        if audio_id:
            audio_ids.add(audio_id)
    return audio_ids


def validate_audio_archive(audio_path: Path, expected_hash: str | None, strict_codec: bool) -> tuple[dict[str, Any], list[str], list[str]]:
    warnings: list[str] = []
    errors: list[str] = []

    if expected_hash:
        actual_hash = sha256_file(audio_path)
        if actual_hash != expected_hash:
            errors.append(f"audio archive hash mismatch: expected {expected_hash}, got {actual_hash}")

    if not zipfile.is_zipfile(audio_path):
        errors.append(f"audio archive is not a ZIP file: {audio_path}")
        return {}, warnings, errors

    with zipfile.ZipFile(audio_path) as archive:
        names = set(archive.namelist())
        if "manifest.json" not in names:
            errors.append("audio archive missing manifest.json")
            return {}, warnings, errors

        manifest = json.loads(archive.read("manifest.json").decode("utf-8"))
        for entry in manifest.get("entries", []):
            audio_id = entry.get("audioId", "<missing>")
            path = entry.get("path")
            codec = entry.get("codec")
            if codec != "flac":
                message = f"audioId {audio_id} uses codec {codec}; NBMS 0.1 final playback requires FLAC"
                if strict_codec:
                    errors.append(message)
                else:
                    warnings.append(message)
            if not path or path not in names:
                errors.append(f"audioId {audio_id} path missing in archive: {path}")
                continue
            expected_entry_hash = entry.get("hash")
            if expected_entry_hash:
                actual_entry_hash = sha256_bytes(archive.read(path))
                if actual_entry_hash != expected_entry_hash:
                    errors.append(
                        f"audioId {audio_id} hash mismatch: expected {expected_entry_hash}, got {actual_entry_hash}"
                    )

    return manifest, warnings, errors


def validate_references(chart: dict[str, Any], audio_manifest: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    manifest_ids = {entry.get("audioId") for entry in audio_manifest.get("entries", [])}
    for audio_id in sorted(collect_audio_ids(chart)):
        if audio_id not in manifest_ids:
            errors.append(f"chart references missing audioId: {audio_id}")
    return errors


def time_at_tick(tick: int, segments: list[tuple[int, float, float]], stops: dict[int, float]) -> float:
    active_tick, active_ms, ms_per_tick = segments[0]
    for segment_tick, segment_ms, segment_ms_per_tick in segments:
        if segment_tick <= tick:
            active_tick = segment_tick
            active_ms = segment_ms
            ms_per_tick = segment_ms_per_tick
        else:
            break
    base = active_ms + (tick - active_tick) * ms_per_tick
    stop_extra = sum(duration for stop_tick, duration in stops.items() if stop_tick < tick)
    return base + stop_extra


def build_timing_map(chart: dict[str, Any]) -> tuple[list[tuple[int, float, float]], dict[int, float]]:
    resolution = chart["resolution"]
    timing = sorted(chart.get("timing", []), key=lambda item: (item["tick"], item["type"]))
    bpm_events = [event for event in timing if event.get("type") == "bpm"]
    stop_events = [event for event in timing if event.get("type") == "stop"]
    if not bpm_events:
        raise ValueError("chart has no BPM event")

    segments: list[tuple[int, float, float]] = []
    current_tick = 0
    current_ms = 0.0
    current_bpm = float(bpm_events[0]["value"])
    current_ms_per_tick = 60000.0 / (current_bpm * resolution)
    segments.append((0, 0.0, current_ms_per_tick))

    for event in bpm_events:
        tick = int(event["tick"])
        if tick == 0 and not segments:
            continue
        if tick < current_tick:
            continue
        current_ms += (tick - current_tick) * current_ms_per_tick
        current_tick = tick
        current_bpm = float(event["value"])
        current_ms_per_tick = 60000.0 / (current_bpm * resolution)
        if segments and segments[-1][0] == tick:
            segments[-1] = (tick, current_ms, current_ms_per_tick)
        else:
            segments.append((tick, current_ms, current_ms_per_tick))

    stops: dict[int, float] = {}
    for event in stop_events:
        tick = int(event["tick"])
        duration_ticks = int(event["durationTicks"])
        ms_per_tick_at_stop = time_segment_for_tick(tick, segments)[2]
        stops[tick] = stops.get(tick, 0.0) + duration_ticks * ms_per_tick_at_stop

    return segments, stops


def time_segment_for_tick(tick: int, segments: list[tuple[int, float, float]]) -> tuple[int, float, float]:
    active = segments[0]
    for segment in segments:
        if segment[0] <= tick:
            active = segment
        else:
            break
    return active


def schedule_events(chart: dict[str, Any]) -> list[ScheduledEvent]:
    segments, stops = build_timing_map(chart)
    events: list[ScheduledEvent] = []

    for timing in chart.get("timing", []):
        tick = int(timing["tick"])
        kind = timing["type"]
        if kind == "bpm":
            detail = f"BPM {timing['value']}"
        elif kind == "bar":
            detail = "bar"
        elif kind == "stop":
            detail = f"stop {timing['durationTicks']} ticks"
        else:
            detail = kind
        events.append(ScheduledEvent(tick, time_at_tick(tick, segments, stops), "timing", detail))

    for audio in chart.get("backgroundAudio", []):
        tick = int(audio["tick"])
        audio_id = audio["audioId"]
        events.append(ScheduledEvent(tick, time_at_tick(tick, segments, stops), "bgm", f"play {audio_id}", audio_id))

    for note in chart.get("notes", []):
        tick = int(note["tick"])
        audio_id = note.get("audioId")
        lane = note["lane"]
        note_type = note["type"]
        detail = f"{note_type} {lane}"
        if audio_id:
            detail += f" audio={audio_id}"
        if note_type == "long":
            detail += f" durationTicks={note.get('durationTicks')}"
        events.append(ScheduledEvent(tick, time_at_tick(tick, segments, stops), "note", detail, audio_id))

    return sorted(events, key=lambda item: (item.time_ms, item.tick, item.kind, item.detail))


def print_summary(header: dict[str, Any], chart_meta: dict[str, Any], chart: dict[str, Any], audio_manifest: dict[str, Any], events: list[ScheduledEvent]) -> None:
    playable_notes = len(chart.get("notes", []))
    bgm_events = len(chart.get("backgroundAudio", []))
    audio_entries = len(audio_manifest.get("entries", []))
    last_ms = max((event.time_ms for event in events), default=0.0)

    print(f"Title: {header.get('title')}")
    print(f"Artist: {header.get('artist')}")
    print(f"Chart: {chart_meta.get('id')} ({chart_meta.get('mode')}, level {chart_meta.get('difficulty')})")
    print(f"Resolution: {chart.get('resolution')} ticks/beat")
    print(f"Notes: {playable_notes}")
    print(f"Background audio events: {bgm_events}")
    print(f"Audio entries: {audio_entries}")
    print(f"Scheduled events: {len(events)}")
    print(f"Last event: {last_ms / 1000:.3f}s")


def print_events(events: list[ScheduledEvent]) -> None:
    for event in events:
        print(f"{event.time_ms / 1000:8.3f}s tick={event.tick:6d} {event.kind:7s} {event.detail}")


def run_realtime(events: list[ScheduledEvent], speed: float) -> None:
    if speed <= 0:
        raise ValueError("--speed must be greater than 0")
    start = time.perf_counter()
    for event in events:
        target = event.time_ms / 1000.0 / speed
        delay = target - (time.perf_counter() - start)
        if delay > 0:
            time.sleep(delay)
        print(f"{event.time_ms / 1000:8.3f}s {event.kind:7s} {event.detail}", flush=True)


def decrypt_entry_if_needed(entry: dict[str, Any], payload: bytes, passphrase: str | None, archive_encryption: dict[str, Any] | None) -> bytes:
    if not entry.get("encrypted"):
        return payload
    if not passphrase:
        raise ValueError(f"audioId {entry.get('audioId')} is encrypted; pass --passphrase")
    if not archive_encryption:
        raise ValueError("audio archive is encrypted but manifest encryption metadata is missing")
    salt = b64d(archive_encryption["salt"])
    iterations = int(archive_encryption["iterations"])
    key = derive_passphrase_key(passphrase, salt, iterations)
    encryption = entry["encryption"]
    return chacha20_poly1305_decrypt(
        key,
        b64d(encryption["nonce"]),
        payload,
        b64d(encryption["tag"]),
        entry["audioId"].encode("utf-8"),
    )


def load_audio_payloads(audio_path: Path, audio_manifest: dict[str, Any], passphrase: str | None) -> dict[str, Pcm16Audio]:
    payloads: dict[str, Pcm16Audio] = {}
    entries = {entry["audioId"]: entry for entry in audio_manifest.get("entries", [])}
    archive_encryption = audio_manifest.get("encryption")
    with zipfile.ZipFile(audio_path) as archive:
        for audio_id, entry in entries.items():
            codec = entry.get("codec")
            path = entry.get("path")
            raw_payload = decrypt_entry_if_needed(entry, archive.read(path), passphrase, archive_encryption)
            if codec == "flac":
                payloads[audio_id] = decode_flac_pcm16(raw_payload)
            elif codec == "pcm-wav":
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
                    temp.write(raw_payload)
                    temp_path = Path(temp.name)
                try:
                    payloads[audio_id] = read_wav_pcm16(temp_path)
                finally:
                    temp_path.unlink(missing_ok=True)
            else:
                raise ValueError(f"cannot decode codec {codec!r} for audioId {audio_id}")
    return payloads


def convert_channels(audio: Pcm16Audio, output_channels: int) -> list[int]:
    if audio.channels == output_channels:
        return audio.samples
    converted: list[int] = []
    for frame in range(audio.frames):
        frame_samples = audio.samples[frame * audio.channels : (frame + 1) * audio.channels]
        if output_channels == 1:
            converted.append(int(sum(frame_samples) / len(frame_samples)))
        elif audio.channels == 1:
            converted.extend([frame_samples[0]] * output_channels)
        else:
            for channel in range(output_channels):
                converted.append(frame_samples[min(channel, audio.channels - 1)])
    return converted


def render_schedule_to_wav(
    events: list[ScheduledEvent],
    audio_path: Path,
    audio_manifest: dict[str, Any],
    output_path: Path,
    passphrase: str | None,
) -> Pcm16Audio:
    payloads = load_audio_payloads(audio_path, audio_manifest, passphrase)
    playable = [event for event in events if event.audio_id]
    if not playable:
        raise ValueError("no playable audio events found")

    sample_rates = {payload.sample_rate for payload in payloads.values()}
    if len(sample_rates) != 1:
        raise ValueError(f"mixed sample rates are not supported yet: {sorted(sample_rates)}")
    sample_rate = sample_rates.pop()
    output_channels = max(payload.channels for payload in payloads.values())

    total_frames = 0
    for event in playable:
        payload = payloads[event.audio_id]
        start_frame = int(event.time_ms * sample_rate / 1000)
        total_frames = max(total_frames, start_frame + payload.frames)
    total_frames += sample_rate
    mix = [0] * (total_frames * output_channels)

    for event in playable:
        payload = payloads[event.audio_id]
        converted = convert_channels(payload, output_channels)
        start_frame = int(event.time_ms * sample_rate / 1000)
        start_index = start_frame * output_channels
        for index, sample in enumerate(converted):
            target = start_index + index
            if target < len(mix):
                mix[target] += sample

    rendered = Pcm16Audio(sample_rate=sample_rate, channels=output_channels, samples=mix)
    write_wav_pcm16(output_path, rendered)
    return rendered


def play_wav(path: Path) -> None:
    if sys.platform.startswith("win"):
        import winsound

        winsound.PlaySound(str(path), winsound.SND_FILENAME)
        return
    raise RuntimeError("--play currently supports Windows only; use --render-wav instead")


def main() -> int:
    args = parse_args()
    header_path = args.header.resolve()
    base_dir = header_path.parent

    try:
        header = load_json(header_path)
        chart_meta = choose_chart(header, args.chart)
        chart_path = base_dir / chart_meta["file"]
        chart = load_json(chart_path)

        expected_chart_hash = chart_meta.get("hash")
        actual_chart_hash = canonical_json_hash(chart)
        errors: list[str] = []
        warnings: list[str] = []
        if expected_chart_hash != actual_chart_hash:
            errors.append(f"chart hash mismatch: expected {expected_chart_hash}, got {actual_chart_hash}")

        if header.get("security", {}).get("signed"):
            public_key_doc = load_crypto_json(args.public_key) if args.public_key else None
            if not verify_header_signature(header, public_key_doc):
                errors.append("header signature verification failed")

        audio_path = base_dir / header["audio"]["file"]
        audio_manifest, audio_warnings, audio_errors = validate_audio_archive(
            audio_path,
            header.get("audio", {}).get("hash"),
            args.strict_codec,
        )
        warnings.extend(audio_warnings)
        errors.extend(audio_errors)
        if audio_manifest:
            errors.extend(validate_references(chart, audio_manifest))

        events = schedule_events(chart)
        print_summary(header, chart_meta, chart, audio_manifest, events)
        print(f"Chart hash: {actual_chart_hash}")

        if warnings:
            print("\nWarnings:")
            for warning in warnings:
                print(f"- {warning}")
        if errors:
            print("\nErrors:", file=sys.stderr)
            for error in errors:
                print(f"- {error}", file=sys.stderr)
            return 1

        if args.events:
            print("\nEvent schedule:")
            print_events(events)
        if args.realtime:
            print("\nRealtime schedule:")
            run_realtime(events, args.speed)
        if args.render_wav or args.play:
            passphrase = args.passphrase
            if audio_manifest.get("encrypted") and not passphrase:
                passphrase = getpass.getpass("Audio decryption passphrase: ")
            if args.render_wav:
                render_path = args.render_wav.resolve()
            else:
                render_path = Path(tempfile.gettempdir()) / "nbms_probe_player_render.wav"
            rendered = render_schedule_to_wav(events, audio_path, audio_manifest, render_path, passphrase)
            print(f"\nRendered WAV: {render_path}")
            print(f"Rendered audio: {rendered.duration_ms / 1000:.3f}s, {rendered.sample_rate} Hz, {rendered.channels} ch")
            if args.play:
                play_wav(render_path)
        return 0
    except Exception as exc:
        print(f"nbms_probe_player: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
