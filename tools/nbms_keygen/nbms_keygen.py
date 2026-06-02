#!/usr/bin/env python3
"""Generate NBMS Phase 4 author keys."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_crypto import new_author_keypair, write_json  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate NBMS author Ed25519 keys.")
    parser.add_argument("key_id")
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()

    private_doc, public_doc = new_author_keypair(args.key_id)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    private_path = args.output_dir / f"{args.key_id}.private.json"
    public_path = args.output_dir / f"{args.key_id}.public.json"
    write_json(private_path, private_doc)
    write_json(public_path, public_doc)
    print(f"Wrote {private_path}")
    print(f"Wrote {public_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

