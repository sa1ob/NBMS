#!/usr/bin/env python3
"""Encrypt NBMS audio archive payloads."""

from __future__ import annotations

import argparse
import getpass
import json
import os
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from nbms_crypto import (  # noqa: E402
    b64e,
    chacha20_poly1305_encrypt,
    derive_passphrase_key,
    sha256_file,
    write_json,
)


def encrypt_archive(source: Path, output: Path, passphrase: str) -> None:
    salt = os.urandom(16)
    iterations = 200_000
    key = derive_passphrase_key(passphrase, salt, iterations)

    with zipfile.ZipFile(source) as archive:
        manifest = json.loads(archive.read("manifest.json").decode("utf-8"))
        encrypted_entries = []
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as encrypted_archive:
            for entry in manifest.get("entries", []):
                original_path = entry["path"]
                plaintext = archive.read(original_path)
                nonce = os.urandom(12)
                aad = entry["audioId"].encode("utf-8")
                ciphertext, tag = chacha20_poly1305_encrypt(key, nonce, plaintext, aad)
                encrypted_path = original_path + ".enc"
                encrypted_archive.writestr(encrypted_path, ciphertext)
                encrypted_entry = {
                    **entry,
                    "path": encrypted_path,
                    "hash": "sha256-" + __import__("hashlib").sha256(ciphertext).hexdigest(),
                    "encrypted": True,
                    "encryption": {
                        "algorithm": "ChaCha20-Poly1305",
                        "nonce": b64e(nonce),
                        "tag": b64e(tag),
                        "aad": "audioId",
                        "originalPath": original_path,
                        "originalHash": entry.get("hash"),
                    },
                }
                encrypted_entries.append(encrypted_entry)

            manifest["encrypted"] = True
            manifest["encryption"] = {
                "algorithm": "ChaCha20-Poly1305",
                "kdf": "PBKDF2-HMAC-SHA256",
                "salt": b64e(salt),
                "iterations": iterations,
            }
            manifest["entries"] = encrypted_entries
            encrypted_archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Encrypt NBMS .nbma audio payloads.")
    parser.add_argument("header", type=Path)
    parser.add_argument("--passphrase")
    parser.add_argument("--output-header", type=Path)
    parser.add_argument("--output-audio", type=Path)
    args = parser.parse_args()

    header_path = args.header.resolve()
    base = header_path.parent
    header = json.loads(header_path.read_text(encoding="utf-8"))
    audio_path = base / header["audio"]["file"]
    passphrase = args.passphrase or getpass.getpass("Audio encryption passphrase: ")

    if args.output_audio:
        output_audio = args.output_audio.resolve()
    else:
        fd, temp_name = tempfile.mkstemp(suffix=".nbma")
        os.close(fd)
        output_audio = Path(temp_name)

    encrypt_archive(audio_path, output_audio, passphrase)

    final_audio_path = audio_path if not args.output_audio else output_audio
    if not args.output_audio:
        shutil.move(str(output_audio), str(audio_path))

    header["audio"]["hash"] = sha256_file(final_audio_path)
    header["security"] = {
        **header.get("security", {}),
        "encrypted": True,
        "editPolicy": "encrypted",
    }
    output_header = args.output_header or header_path
    write_json(output_header, header)
    print(f"Encrypted {final_audio_path}")
    print(f"Updated {output_header}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

