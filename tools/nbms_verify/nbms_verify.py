#!/usr/bin/env python3
"""Verify NBMS Phase 4 package hashes and signatures."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_crypto import canonical_json_bytes, load_json, sha256_file, verify_header_signature  # noqa: E402


def canonical_json_hash(value: Any) -> str:
    return "sha256-" + hashlib.sha256(canonical_json_bytes(value)).hexdigest()


def sha256_bytes(data: bytes) -> str:
    return "sha256-" + hashlib.sha256(data).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify an NBMS package.")
    parser.add_argument("header", type=Path)
    parser.add_argument("--public-key", type=Path)
    args = parser.parse_args()

    base = args.header.resolve().parent
    header = load_json(args.header)
    public_key = load_json(args.public_key) if args.public_key else None
    errors: list[str] = []

    audio_path = base / header["audio"]["file"]
    actual_audio_hash = sha256_file(audio_path)
    if actual_audio_hash != header["audio"]["hash"]:
        errors.append(f"audio hash mismatch: {actual_audio_hash} != {header['audio']['hash']}")

    for chart_meta in header.get("charts", []):
        chart = load_json(base / chart_meta["file"])
        actual_chart_hash = canonical_json_hash(chart)
        if actual_chart_hash != chart_meta["hash"]:
            errors.append(f"chart hash mismatch for {chart_meta['id']}: {actual_chart_hash} != {chart_meta['hash']}")

    if zipfile.is_zipfile(audio_path):
        with zipfile.ZipFile(audio_path) as archive:
            manifest = json.loads(archive.read("manifest.json").decode("utf-8"))
            names = set(archive.namelist())
            for entry in manifest.get("entries", []):
                path = entry.get("path")
                if not path or path not in names:
                    errors.append(f"missing audio entry {entry.get('audioId')}: {path}")
                    continue
                actual_entry_hash = sha256_bytes(archive.read(path))
                expected_entry_hash = entry.get("hash")
                if expected_entry_hash and actual_entry_hash != expected_entry_hash:
                    errors.append(f"audio entry hash mismatch for {entry.get('audioId')}")

    signed = header.get("security", {}).get("signed")
    if signed:
        if not verify_header_signature(header, public_key):
            errors.append("header signature verification failed")
    else:
        errors.append("header is not signed")

    if errors:
        print("Verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Verification passed")
    if signed:
        print(f"Signature key id: {header.get('security', {}).get('publicKeyId')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

