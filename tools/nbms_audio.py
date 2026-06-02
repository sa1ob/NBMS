"""Small audio helpers for NBMS prototype tools.

The FLAC implementation intentionally supports a narrow subset: 16-bit PCM
encoded with verbatim subframes. This keeps Phase 3 self-contained while still
producing real FLAC containers that can later be replaced by a production codec.
"""

from __future__ import annotations

import hashlib
import struct
import wave
from dataclasses import dataclass
from pathlib import Path


DEFAULT_BLOCK_SIZE = 4096


@dataclass(frozen=True)
class Pcm16Audio:
    sample_rate: int
    channels: int
    samples: list[int]

    @property
    def frames(self) -> int:
        return len(self.samples) // self.channels if self.channels else 0

    @property
    def duration_ms(self) -> int:
        return int(self.frames * 1000 / self.sample_rate) if self.sample_rate else 0


class BitWriter:
    def __init__(self) -> None:
        self.data = bytearray()
        self.current = 0
        self.used = 0

    def write(self, value: int, bits: int) -> None:
        for bit_index in range(bits - 1, -1, -1):
            self.current = (self.current << 1) | ((value >> bit_index) & 1)
            self.used += 1
            if self.used == 8:
                self.data.append(self.current)
                self.current = 0
                self.used = 0

    def align(self) -> None:
        if self.used:
            self.current <<= 8 - self.used
            self.data.append(self.current)
            self.current = 0
            self.used = 0

    def bytes(self) -> bytes:
        self.align()
        return bytes(self.data)


class BitReader:
    def __init__(self, data: bytes, offset: int = 0) -> None:
        self.data = data
        self.byte_pos = offset
        self.bit_pos = 0

    def read(self, bits: int) -> int:
        value = 0
        for _ in range(bits):
            if self.byte_pos >= len(self.data):
                raise EOFError("unexpected end of FLAC data")
            bit = (self.data[self.byte_pos] >> (7 - self.bit_pos)) & 1
            value = (value << 1) | bit
            self.bit_pos += 1
            if self.bit_pos == 8:
                self.byte_pos += 1
                self.bit_pos = 0
        return value

    def align(self) -> None:
        if self.bit_pos:
            self.byte_pos += 1
            self.bit_pos = 0

    def read_byte_aligned(self) -> int:
        self.align()
        value = self.data[self.byte_pos]
        self.byte_pos += 1
        return value


def read_wav_pcm16(path: Path) -> Pcm16Audio:
    with wave.open(str(path), "rb") as handle:
        channels = handle.getnchannels()
        sample_width = handle.getsampwidth()
        sample_rate = handle.getframerate()
        frames = handle.getnframes()
        if sample_width != 2:
            raise ValueError(f"only 16-bit WAV is supported: {path}")
        raw = handle.readframes(frames)
    samples = list(struct.unpack("<" + "h" * (len(raw) // 2), raw))
    return Pcm16Audio(sample_rate=sample_rate, channels=channels, samples=samples)


def write_wav_pcm16(path: Path, audio: Pcm16Audio) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    clipped = [max(-32768, min(32767, int(sample))) for sample in audio.samples]
    raw = struct.pack("<" + "h" * len(clipped), *clipped)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(audio.channels)
        handle.setsampwidth(2)
        handle.setframerate(audio.sample_rate)
        handle.writeframes(raw)


def crc8(data: bytes) -> int:
    crc = 0
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
    return crc


def crc16(data: bytes) -> int:
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x8005) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def utf8_uint(value: int) -> bytes:
    if value < 0x80:
        return bytes([value])
    if value < 0x800:
        return bytes([0xC0 | (value >> 6), 0x80 | (value & 0x3F)])
    if value < 0x10000:
        return bytes([0xE0 | (value >> 12), 0x80 | ((value >> 6) & 0x3F), 0x80 | (value & 0x3F)])
    raise ValueError("frame number too large for prototype FLAC encoder")


def read_utf8_uint(reader: BitReader) -> int:
    first = reader.read_byte_aligned()
    if first < 0x80:
        return first
    if first & 0xE0 == 0xC0:
        return ((first & 0x1F) << 6) | (reader.read_byte_aligned() & 0x3F)
    if first & 0xF0 == 0xE0:
        return ((first & 0x0F) << 12) | ((reader.read_byte_aligned() & 0x3F) << 6) | (reader.read_byte_aligned() & 0x3F)
    raise ValueError("unsupported FLAC UTF-8 integer")


def streaminfo(audio: Pcm16Audio, min_block: int, max_block: int) -> bytes:
    raw_pcm = struct.pack("<" + "h" * len(audio.samples), *audio.samples)
    md5 = hashlib.md5(raw_pcm).digest()
    packed = bytearray()
    packed += min_block.to_bytes(2, "big")
    packed += max_block.to_bytes(2, "big")
    packed += b"\x00\x00\x00"
    packed += b"\x00\x00\x00"
    combined = (
        (audio.sample_rate & 0xFFFFF) << 44
        | ((audio.channels - 1) & 0x7) << 41
        | (15 << 36)
        | (audio.frames & 0xFFFFFFFFF)
    )
    packed += combined.to_bytes(8, "big")
    packed += md5
    return bytes(packed)


def encode_frame(audio: Pcm16Audio, start_frame: int, block_size: int, frame_number: int) -> bytes:
    header = bytearray()
    bw = BitWriter()
    bw.write(0x3FFE, 14)
    bw.write(0, 1)
    bw.write(0, 1)
    bw.write(7, 4)
    bw.write(0, 4)
    bw.write(audio.channels - 1, 4)
    bw.write(4, 3)
    bw.write(0, 1)
    header += bw.bytes()
    header += utf8_uint(frame_number)
    header += (block_size - 1).to_bytes(2, "big")
    header.append(crc8(header))

    subframes = BitWriter()
    for channel in range(audio.channels):
        subframes.write(0, 1)
        subframes.write(1, 6)
        subframes.write(0, 1)
        for frame in range(start_frame, start_frame + block_size):
            sample = audio.samples[frame * audio.channels + channel]
            subframes.write(sample & 0xFFFF, 16)
    payload = subframes.bytes()
    frame = bytes(header) + payload
    return frame + crc16(frame).to_bytes(2, "big")


def encode_flac_pcm16(audio: Pcm16Audio, output_path: Path, block_size: int = DEFAULT_BLOCK_SIZE) -> None:
    if audio.channels < 1 or audio.channels > 8:
        raise ValueError("FLAC prototype supports 1 to 8 channels")
    blocks = [min(block_size, audio.frames - start) for start in range(0, audio.frames, block_size)]
    if not blocks:
        blocks = [0]
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("wb") as handle:
        handle.write(b"fLaC")
        metadata_header = bytes([0x80]) + len(streaminfo(audio, min(blocks), max(blocks))).to_bytes(3, "big")
        handle.write(metadata_header)
        handle.write(streaminfo(audio, min(blocks), max(blocks)))
        for frame_number, start in enumerate(range(0, audio.frames, block_size)):
            current_block = min(block_size, audio.frames - start)
            handle.write(encode_frame(audio, start, current_block, frame_number))


def decode_flac_pcm16(data: bytes) -> Pcm16Audio:
    if not data.startswith(b"fLaC"):
        raise ValueError("not a FLAC stream")
    offset = 4
    sample_rate = channels = bits_per_sample = total_samples = 0
    while True:
        header = data[offset]
        offset += 1
        is_last = bool(header & 0x80)
        block_type = header & 0x7F
        length = int.from_bytes(data[offset : offset + 3], "big")
        offset += 3
        payload = data[offset : offset + length]
        offset += length
        if block_type == 0:
            combined = int.from_bytes(payload[10:18], "big")
            sample_rate = (combined >> 44) & 0xFFFFF
            channels = ((combined >> 41) & 0x7) + 1
            bits_per_sample = ((combined >> 36) & 0x1F) + 1
            total_samples = combined & 0xFFFFFFFFF
        if is_last:
            break
    if bits_per_sample != 16:
        raise ValueError("prototype decoder only supports 16-bit FLAC")

    channel_samples = [[] for _ in range(channels)]
    while offset < len(data) and (total_samples == 0 or len(channel_samples[0]) < total_samples):
        reader = BitReader(data, offset)
        if reader.read(14) != 0x3FFE:
            raise ValueError("FLAC frame sync not found")
        reader.read(1)
        reader.read(1)
        block_size_code = reader.read(4)
        reader.read(4)
        channel_assignment = reader.read(4)
        sample_size_code = reader.read(3)
        reader.read(1)
        read_utf8_uint(reader)
        if block_size_code == 7:
            block_size = (reader.read_byte_aligned() << 8 | reader.read_byte_aligned()) + 1
        elif block_size_code == 6:
            block_size = reader.read_byte_aligned() + 1
        else:
            raise ValueError("prototype decoder only supports explicit FLAC block sizes")
        reader.read_byte_aligned()
        if channel_assignment != channels - 1 or sample_size_code != 4:
            raise ValueError("unsupported FLAC frame layout")

        for channel in range(channels):
            if reader.read(1) != 0:
                raise ValueError("invalid FLAC subframe padding")
            subframe_type = reader.read(6)
            wasted = reader.read(1)
            if subframe_type != 1 or wasted:
                raise ValueError("prototype decoder only supports verbatim subframes")
            for _ in range(block_size):
                sample = reader.read(16)
                if sample >= 0x8000:
                    sample -= 0x10000
                channel_samples[channel].append(sample)
        reader.align()
        reader.read(16)
        offset = reader.byte_pos

    samples: list[int] = []
    frames = min(len(channel) for channel in channel_samples) if channel_samples else 0
    for frame in range(frames):
        for channel in range(channels):
            samples.append(channel_samples[channel][frame])
    return Pcm16Audio(sample_rate=sample_rate, channels=channels, samples=samples)


def convert_wav_to_flac(wav_path: Path, flac_path: Path) -> Pcm16Audio:
    audio = read_wav_pcm16(wav_path)
    encode_flac_pcm16(audio, flac_path)
    return audio

