#!/usr/bin/env python3
"""Sign an NBMS header."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_crypto import load_json, sign_header, write_json  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description="Sign an NBMS .nbmh header.")
    parser.add_argument("header", type=Path)
    parser.add_argument("private_key", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    header = load_json(args.header)
    private_key = load_json(args.private_key)
    signed = sign_header(header, private_key)
    output = args.output or args.header
    write_json(output, signed)
    print(f"Signed {output}")
    print(f"Key id: {private_key['keyId']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

