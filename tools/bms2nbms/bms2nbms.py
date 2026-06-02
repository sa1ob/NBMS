#!/usr/bin/env python3
"""Prototype BMS to NBMS 0.1 converter."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
import wave
import zipfile
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_audio import convert_wav_to_flac, decode_flac_pcm16  # noqa: E402


RESOLUTION = 960
BEATS_PER_MEASURE = 4
MEASURE_TICKS = RESOLUTION * BEATS_PER_MEASURE
BASE36 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"


HEADER_RE = re.compile(r"^#([A-Za-z][A-Za-z0-9]*)(?:\s+(.*))?$")
CHANNEL_RE = re.compile(r"^#([0-9]{3})([0-9A-Z]{2}):(.*)$", re.IGNORECASE)


NOTE_CHANNELS = {
    "11": "key1",
    "12": "key2",
    "13": "key3",
    "14": "key4",
    "15": "key5",
    "16": "scratch",
    "18": "key6",
    "19": "key7",
}

LONG_NOTE_CHANNELS = {
    "51": "key1",
    "52": "key2",
    "53": "key3",
    "54": "key4",
    "55": "key5",
    "56": "scratch",
    "58": "key6",
    "59": "key7",
}


@dataclass
class ChannelEvent:
    measure: int
    channel: str
    data: str
    line_no: int


@dataclass
class BmsDocument:
    source: Path
    title: str = "Untitled"
    subtitle: str = ""
    artist: str = "Unknown Artist"
    genre: str = ""
    bpm: Decimal = Decimal("130")
    playlevel: int = 0
    wav: dict[str, str] = field(default_factory=dict)
    bpm_defs: dict[str, Decimal] = field(default_factory=dict)
    stop_defs: dict[str, int] = field(default_factory=dict)
    events: list[ChannelEvent] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert a BMS file to NBMS 0.1.")
    parser.add_argument("input", type=Path, help="Input .bms/.bme/.bml file")
    parser.add_argument("output", type=Path, help="Output directory")
    parser.add_argument("--chart-id", default="main", help="Output chart id")
    parser.add_argument("--song-id", default=None, help="NBMS song id")
    parser.add_argument("--license", default="Converted from BMS; rights unspecified")
    parser.add_argument("--sound-source", default="Converted BMS audio")
    parser.add_argument(
        "--no-flac-transcode",
        action="store_true",
        help="Store source audio as-is instead of converting WAV PCM16 files to FLAC.",
    )
    parser.add_argument("--force", action="store_true", help="Overwrite output directory contents")
    return parser.parse_args()


def read_text(path: Path) -> str:
    for encoding in ("utf-8-sig", "cp932", "latin-1"):
        try:
            return path.read_text(encoding=encoding)
        except UnicodeDecodeError:
            continue
    return path.read_text(errors="replace")


def parse_bms(path: Path) -> BmsDocument:
    doc = BmsDocument(source=path)
    for line_no, raw_line in enumerate(read_text(path).splitlines(), start=1):
        line = raw_line.strip()
        if not line or not line.startswith("#"):
            continue

        channel_match = CHANNEL_RE.match(line)
        if channel_match:
            measure, channel, data = channel_match.groups()
            doc.events.append(
                ChannelEvent(
                    measure=int(measure),
                    channel=channel.upper(),
                    data=data.strip().upper(),
                    line_no=line_no,
                )
            )
            continue

        header_match = HEADER_RE.match(line)
        if not header_match:
            continue
        key, value = header_match.groups()
        key = key.upper()
        value = (value or "").strip()

        if key == "TITLE":
            doc.title = value or doc.title
        elif key == "SUBTITLE":
            doc.subtitle = value
        elif key == "ARTIST":
            doc.artist = value or doc.artist
        elif key == "GENRE":
            doc.genre = value
        elif key == "BPM":
            try:
                doc.bpm = Decimal(value)
            except InvalidOperation:
                doc.warnings.append(f"Line {line_no}: invalid #BPM value {value!r}")
        elif key.startswith("BPM") and len(key) == 5:
            try:
                doc.bpm_defs[key[3:].upper()] = Decimal(value)
            except InvalidOperation:
                doc.warnings.append(f"Line {line_no}: invalid #{key} value {value!r}")
        elif key.startswith("WAV") and len(key) == 5:
            doc.wav[key[3:].upper()] = value
        elif key.startswith("STOP") and len(key) == 6:
            try:
                doc.stop_defs[key[4:].upper()] = int(value, 36)
            except ValueError:
                doc.warnings.append(f"Line {line_no}: invalid #{key} value {value!r}")
        elif key == "PLAYLEVEL":
            try:
                doc.playlevel = int(Decimal(value))
            except InvalidOperation:
                doc.warnings.append(f"Line {line_no}: invalid #PLAYLEVEL value {value!r}")

    return doc


def base36_to_int(value: str) -> int:
    result = 0
    for char in value.upper():
        result = result * 36 + BASE36.index(char)
    return result


def safe_id(value: str, fallback: str) -> str:
    normalized = re.sub(r"[^a-zA-Z0-9._:-]+", "_", value.strip())
    normalized = normalized.strip("._:-")
    return normalized or fallback


def audio_id_for(wav_key: str, filename: str) -> str:
    stem = safe_id(Path(filename).stem, "audio")
    return f"wav_{wav_key.lower()}_{stem}"


def detect_codec(path: Path) -> str:
    suffix = path.suffix.lower()
    if suffix == ".flac":
        return "flac"
    if suffix == ".ogg":
        return "opus"
    if suffix == ".wav":
        return "pcm-wav"
    return suffix.lstrip(".") or "unknown"


def wav_info(path: Path) -> tuple[int, int, int]:
    if path.suffix.lower() != ".wav":
        return 0, 0, 0
    try:
        with wave.open(str(path), "rb") as wf:
            frames = wf.getnframes()
            rate = wf.getframerate()
            duration_ms = int(frames * 1000 / rate) if rate else 0
            return rate, wf.getnchannels(), duration_ms
    except (wave.Error, OSError):
        return 0, 0, 0


def flac_info(path: Path) -> tuple[int, int, int]:
    try:
        audio = decode_flac_pcm16(path.read_bytes())
        return audio.sample_rate, audio.channels, audio.duration_ms
    except (ValueError, OSError):
        return 0, 0, 0


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

    data = json.dumps(canonical(value), ensure_ascii=False, separators=(",", ":"))
    return "sha256-" + hashlib.sha256(data.encode("utf-8")).hexdigest()


def event_tick(event: ChannelEvent, measure_offsets: dict[int, int], measure_lengths: dict[int, int], index: int, count: int) -> int:
    measure_start = measure_offsets[event.measure]
    measure_length = measure_lengths.get(event.measure, MEASURE_TICKS)
    return measure_start + round(measure_length * index / count)


def build_measure_lengths(doc: BmsDocument) -> dict[int, int]:
    lengths: dict[int, int] = {}
    for event in doc.events:
        if event.channel != "02":
            continue
        try:
            multiplier = Decimal(event.data)
        except InvalidOperation:
            doc.warnings.append(f"Line {event.line_no}: invalid measure length {event.data!r}")
            continue
        lengths[event.measure] = int(MEASURE_TICKS * multiplier)
    return lengths


def build_measure_offsets(doc: BmsDocument, measure_lengths: dict[int, int]) -> dict[int, int]:
    max_measure = max((event.measure for event in doc.events), default=0)
    offsets: dict[int, int] = {}
    current = 0
    for measure in range(max_measure + 2):
        offsets[measure] = current
        current += measure_lengths.get(measure, MEASURE_TICKS)
    return offsets


def iter_tokens(data: str) -> list[str]:
    if len(data) % 2 != 0:
        return []
    return [data[index : index + 2] for index in range(0, len(data), 2)]


def build_chart(doc: BmsDocument, chart_id: str) -> dict[str, Any]:
    wav_to_audio = {key: audio_id_for(key, filename) for key, filename in doc.wav.items()}
    measure_lengths = build_measure_lengths(doc)
    measure_offsets = build_measure_offsets(doc, measure_lengths)

    timing: list[dict[str, Any]] = [
        {"tick": 0, "type": "bpm", "value": float(doc.bpm)},
    ]
    for measure, tick in sorted(measure_offsets.items()):
        if measure <= max((event.measure for event in doc.events), default=0):
            timing.append({"tick": tick, "type": "bar"})

    notes: list[dict[str, Any]] = []
    background_audio: list[dict[str, Any]] = []

    long_starts: dict[tuple[str, str | None], dict[str, Any]] = {}

    for event in sorted(doc.events, key=lambda e: (e.measure, e.channel, e.line_no)):
        if event.channel == "02":
            continue
        tokens = iter_tokens(event.data)
        if not tokens:
            doc.warnings.append(f"Line {event.line_no}: odd-length channel data skipped")
            continue

        for index, token in enumerate(tokens):
            if token == "00":
                continue
            tick = event_tick(event, measure_offsets, measure_lengths, index, len(tokens))

            if event.channel == "01":
                audio_id = wav_to_audio.get(token)
                if audio_id:
                    background_audio.append({"tick": tick, "audioId": audio_id})
                else:
                    doc.warnings.append(f"Line {event.line_no}: undefined #WAV{token}")
            elif event.channel == "03":
                timing.append({"tick": tick, "type": "bpm", "value": base36_to_int(token)})
            elif event.channel == "08":
                bpm = doc.bpm_defs.get(token)
                if bpm is None:
                    doc.warnings.append(f"Line {event.line_no}: undefined #BPM{token}")
                else:
                    timing.append({"tick": tick, "type": "bpm", "value": float(bpm)})
            elif event.channel == "09":
                stop_value = doc.stop_defs.get(token)
                if stop_value is None:
                    doc.warnings.append(f"Line {event.line_no}: undefined #STOP{token}")
                else:
                    timing.append({"tick": tick, "type": "stop", "durationTicks": stop_value})
            elif event.channel in NOTE_CHANNELS:
                audio_id = wav_to_audio.get(token)
                if not audio_id:
                    doc.warnings.append(f"Line {event.line_no}: undefined #WAV{token}")
                    continue
                notes.append(
                    {
                        "tick": tick,
                        "lane": NOTE_CHANNELS[event.channel],
                        "type": "tap",
                        "audioId": audio_id,
                    }
                )
            elif event.channel in LONG_NOTE_CHANNELS:
                lane = LONG_NOTE_CHANNELS[event.channel]
                audio_id = wav_to_audio.get(token)
                long_key = (lane, audio_id)
                if long_key in long_starts:
                    start = long_starts.pop(long_key)
                    duration = max(1, tick - int(start["tick"]))
                    start["durationTicks"] = duration
                    notes.append(start)
                else:
                    if not audio_id:
                        doc.warnings.append(f"Line {event.line_no}: undefined #WAV{token}")
                        continue
                    long_starts[long_key] = {
                        "tick": tick,
                        "lane": lane,
                        "type": "long",
                        "audioId": audio_id,
                    }
            else:
                doc.warnings.append(f"Line {event.line_no}: unsupported channel {event.channel}")

    for (_, _), start in long_starts.items():
        doc.warnings.append(f"Unclosed long note at tick {start['tick']} on lane {start['lane']} skipped")

    timing.sort(key=lambda item: (item["tick"], item["type"]))
    notes.sort(key=lambda item: (item["tick"], item["lane"]))
    background_audio.sort(key=lambda item: item["tick"])

    return {
        "format": "NBMS-CHART",
        "version": "0.1.0",
        "chartId": chart_id,
        "mode": "beat-7k",
        "resolution": RESOLUTION,
        "lanes": [
            {"id": "key1", "type": "key", "index": 1},
            {"id": "key2", "type": "key", "index": 2},
            {"id": "key3", "type": "key", "index": 3},
            {"id": "key4", "type": "key", "index": 4},
            {"id": "key5", "type": "key", "index": 5},
            {"id": "key6", "type": "key", "index": 6},
            {"id": "key7", "type": "key", "index": 7},
            {"id": "scratch", "type": "scratch", "index": 8},
        ],
        "timing": timing,
        "notes": notes,
        "backgroundAudio": background_audio,
        "mediaEvents": [],
        "metadata": {
            "source": doc.source.name,
            "converter": "tools/bms2nbms",
        },
    }


def make_audio_archive(doc: BmsDocument, output_path: Path, transcode_wav_to_flac: bool) -> str:
    source_dir = doc.source.parent
    entries: list[dict[str, Any]] = []
    temp_dir = output_path.parent / "_audio_pack"
    if temp_dir.exists():
        shutil.rmtree(temp_dir)
    (temp_dir / "audio").mkdir(parents=True)

    try:
        for key, filename in sorted(doc.wav.items()):
            source_file = source_dir / filename
            audio_id = audio_id_for(key, filename)
            codec = detect_codec(source_file)
            if not source_file.exists():
                doc.warnings.append(f"Missing audio file for #WAV{key}: {filename}")
                archive_name = f"audio/{audio_id}{source_file.suffix.lower()}"
                entry_hash = "sha256-" + "0" * 64
                sample_rate = channels = duration_ms = 0
            else:
                if codec == "pcm-wav" and transcode_wav_to_flac:
                    archive_name = f"audio/{audio_id}.flac"
                    target_file = temp_dir / archive_name
                    audio = convert_wav_to_flac(source_file, target_file)
                    codec = "flac"
                    sample_rate = audio.sample_rate
                    channels = audio.channels
                    duration_ms = audio.duration_ms
                    entry_hash = sha256_file(target_file)
                else:
                    archive_name = f"audio/{audio_id}{source_file.suffix.lower()}"
                    target_file = temp_dir / archive_name
                    target_file.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source_file, target_file)
                    entry_hash = sha256_file(target_file)
                    if codec == "flac":
                        sample_rate, channels, duration_ms = flac_info(source_file)
                    else:
                        sample_rate, channels, duration_ms = wav_info(source_file)
                if codec != "flac":
                    doc.warnings.append(
                        f"#WAV{key} uses {codec}; NBMS 0.1 playback requires FLAC and this file was stored as-is"
                    )

            entries.append(
                {
                    "audioId": audio_id,
                    "path": archive_name,
                    "codec": codec,
                    "sampleRate": sample_rate,
                    "channels": channels,
                    "durationMs": duration_ms,
                    "hash": entry_hash,
                    "rightsId": "converted-bms-audio",
                }
            )

        manifest = {
            "format": "NBMS-AUDIO",
            "version": "0.1.0",
            "codecRequired": ["flac"],
            "encrypted": False,
            "entries": entries,
            "rights": [
                {
                    "id": "converted-bms-audio",
                    "name": "Converted BMS audio",
                    "role": "sound-source",
                    "license": "Rights unspecified; check original BMS package",
                }
            ],
        }
        (temp_dir / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

        with zipfile.ZipFile(output_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.write(temp_dir / "manifest.json", "manifest.json")
            for file_path in sorted((temp_dir / "audio").glob("*")):
                archive.write(file_path, f"audio/{file_path.name}")
    finally:
        if temp_dir.exists():
            shutil.rmtree(temp_dir)

    return sha256_file(output_path)


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def convert(args: argparse.Namespace) -> int:
    input_path = args.input.resolve()
    output_dir = args.output.resolve()
    if not input_path.exists():
        print(f"Input not found: {input_path}", file=sys.stderr)
        return 2
    if output_dir.exists() and args.force:
        shutil.rmtree(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "charts").mkdir(exist_ok=True)

    doc = parse_bms(input_path)
    chart = build_chart(doc, args.chart_id)
    chart_hash = canonical_json_hash(chart)
    audio_hash = make_audio_archive(doc, output_dir / "audio.nbma", not args.no_flac_transcode)

    song_id = args.song_id or "converted." + safe_id(input_path.stem.lower(), "song")
    header = {
        "format": "NBMS",
        "version": "0.1.0",
        "id": song_id,
        "title": doc.title,
        "subtitle": doc.subtitle,
        "artist": doc.artist,
        "genre": doc.genre,
        "bpm": {
            "initial": float(doc.bpm),
            "min": float(doc.bpm),
            "max": float(doc.bpm),
        },
        "audio": {
            "file": "audio.nbma",
            "hash": audio_hash,
        },
        "charts": [
            {
                "id": args.chart_id,
                "file": f"charts/{args.chart_id}.nbmc",
                "mode": "beat-7k",
                "difficulty": doc.playlevel,
                "levelName": args.chart_id,
                "hash": chart_hash,
                "hashAlgorithm": "sha256-canonical-json",
            }
        ],
        "rights": {
            "music": doc.artist,
            "chart": doc.artist,
            "soundSource": args.sound_source,
            "license": args.license,
            "contact": "",
        },
        "security": {
            "signed": False,
            "encrypted": False,
            "editPolicy": "open",
        },
    }

    write_json(output_dir / "charts" / f"{args.chart_id}.nbmc", chart)
    write_json(output_dir / "song.nbmh", header)

    print(f"Wrote {output_dir / 'song.nbmh'}")
    print(f"Wrote {output_dir / 'charts' / (args.chart_id + '.nbmc')}")
    print(f"Wrote {output_dir / 'audio.nbma'}")
    print(f"Chart hash: {chart_hash}")
    if doc.warnings:
        print("\nWarnings:", file=sys.stderr)
        for warning in doc.warnings:
            print(f"- {warning}", file=sys.stderr)
    return 0


def main() -> int:
    return convert(parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
