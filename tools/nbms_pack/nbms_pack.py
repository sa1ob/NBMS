#!/usr/bin/env python3
"""Create NBMS distribution packages."""

from __future__ import annotations

import argparse
import json
import zipfile
from pathlib import Path
from typing import Any


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def collect_package_files(header_path: Path) -> list[tuple[Path, str]]:
    base = header_path.resolve().parent
    header = load_json(header_path)
    files: list[tuple[Path, str]] = [(header_path.resolve(), "song.nbmh")]

    audio_file = base / header["audio"]["file"]
    files.append((audio_file.resolve(), header["audio"]["file"].replace("\\", "/")))

    media = header.get("media")
    if media:
        media_file = base / media["file"]
        if media_file.exists():
            files.append((media_file.resolve(), media["file"].replace("\\", "/")))

    for chart in header.get("charts", []):
        chart_file = base / chart["file"]
        files.append((chart_file.resolve(), chart["file"].replace("\\", "/")))

    return files


def create_package(header_path: Path, output_path: Path) -> None:
    header = load_json(header_path)
    files = collect_package_files(header_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    manifest = {
        "format": "NBMS-PACKAGE",
        "version": "0.1.0",
        "id": header.get("id"),
        "title": header.get("title"),
        "header": "song.nbmh",
        "files": [archive_name for _, archive_name in files],
    }

    with zipfile.ZipFile(output_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("package_manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        seen: set[str] = set()
        for source, archive_name in files:
            if archive_name in seen:
                continue
            seen.add(archive_name)
            archive.write(source, archive_name)


def main() -> int:
    parser = argparse.ArgumentParser(description="Create an NBMS .nbmp distribution package.")
    parser.add_argument("header", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    create_package(args.header, args.output)
    print(f"Wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

