"""Prototype cryptographic helpers for NBMS Phase 4.

This module is self-contained for the current workspace. Production NBMS tools
should replace it with audited cryptographic libraries.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import struct
from pathlib import Path
from typing import Any


P = 2**255 - 19
Q = 2**252 + 27742317777372353535851937790883648493
D = -121665 * pow(121666, P - 2, P) % P
I = pow(2, (P - 1) // 4, P)
B = (
    15112221349535400772501151409588531511454012693041857206046113283949847762202,
    46316835694926478169428394003475163141307993866256225615783033603165251855960,
)


def b64e(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def b64d(data: str) -> bytes:
    return base64.b64decode(data.encode("ascii"))


def canonical_json_bytes(value: Any) -> bytes:
    def canonical(obj: Any) -> Any:
        if isinstance(obj, dict):
            return {key: canonical(obj[key]) for key in sorted(obj)}
        if isinstance(obj, list):
            return [canonical(item) for item in obj]
        return obj

    return json.dumps(canonical(value), ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return "sha256-" + digest.hexdigest()


def modp_inv(x: int) -> int:
    return pow(x, P - 2, P)


def point_add(p: tuple[int, int], q: tuple[int, int]) -> tuple[int, int]:
    x1, y1 = p
    x2, y2 = q
    denom = modp_inv(1 + D * x1 * x2 * y1 * y2)
    x3 = (x1 * y2 + x2 * y1) * denom % P
    denom = modp_inv(1 - D * x1 * x2 * y1 * y2)
    y3 = (y1 * y2 + x1 * x2) * denom % P
    return x3, y3


def point_mul(s: int, p: tuple[int, int] = B) -> tuple[int, int]:
    result = (0, 1)
    addend = p
    while s:
        if s & 1:
            result = point_add(result, addend)
        addend = point_add(addend, addend)
        s >>= 1
    return result


def recover_x(y: int, sign: int) -> int:
    xx = (y * y - 1) * modp_inv(D * y * y + 1) % P
    x = pow(xx, (P + 3) // 8, P)
    if (x * x - xx) % P != 0:
        x = x * I % P
    if x & 1 != sign:
        x = P - x
    return x


def point_compress(p: tuple[int, int]) -> bytes:
    x, y = p
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def point_decompress(data: bytes) -> tuple[int, int]:
    if len(data) != 32:
        raise ValueError("Ed25519 public key must be 32 bytes")
    y = int.from_bytes(data, "little") & ((1 << 255) - 1)
    sign = data[31] >> 7
    if y >= P:
        raise ValueError("invalid Ed25519 point")
    return recover_x(y, sign), y


def secret_expand(seed: bytes) -> tuple[int, bytes]:
    if len(seed) != 32:
        raise ValueError("Ed25519 seed must be 32 bytes")
    digest = hashlib.sha512(seed).digest()
    a = int.from_bytes(digest[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, digest[32:]


def ed25519_public_key(seed: bytes) -> bytes:
    a, _ = secret_expand(seed)
    return point_compress(point_mul(a))


def ed25519_sign(seed: bytes, message: bytes) -> bytes:
    a, prefix = secret_expand(seed)
    public_key = point_compress(point_mul(a))
    r = int.from_bytes(hashlib.sha512(prefix + message).digest(), "little") % Q
    r_point = point_compress(point_mul(r))
    h = int.from_bytes(hashlib.sha512(r_point + public_key + message).digest(), "little") % Q
    s = (r + h * a) % Q
    return r_point + int.to_bytes(s, 32, "little")


def ed25519_verify(public_key: bytes, message: bytes, signature: bytes) -> bool:
    if len(public_key) != 32 or len(signature) != 64:
        return False
    try:
        a_point = point_decompress(public_key)
        r_point = point_decompress(signature[:32])
    except ValueError:
        return False
    s = int.from_bytes(signature[32:], "little")
    if s >= Q:
        return False
    h = int.from_bytes(hashlib.sha512(signature[:32] + public_key + message).digest(), "little") % Q
    return point_mul(s) == point_add(r_point, point_mul(h, a_point))


def new_author_keypair(key_id: str) -> tuple[dict[str, Any], dict[str, Any]]:
    seed = os.urandom(32)
    public = ed25519_public_key(seed)
    private_doc = {
        "format": "NBMS-AUTHOR-PRIVATE",
        "version": "0.1.0",
        "keyId": key_id,
        "algorithm": "Ed25519",
        "seed": b64e(seed),
        "publicKey": b64e(public),
    }
    public_doc = {
        "format": "NBMS-AUTHOR-PUBLIC",
        "version": "0.1.0",
        "keyId": key_id,
        "algorithm": "Ed25519",
        "publicKey": b64e(public),
    }
    return private_doc, public_doc


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def signing_payload(header: dict[str, Any]) -> bytes:
    clone = json.loads(json.dumps(header))
    security = clone.setdefault("security", {})
    security.pop("signature", None)
    return canonical_json_bytes(clone)


def sign_header(header: dict[str, Any], private_key: dict[str, Any]) -> dict[str, Any]:
    seed = b64d(private_key["seed"])
    public_key = private_key.get("publicKey") or b64e(ed25519_public_key(seed))
    signed = json.loads(json.dumps(header))
    signed["security"] = {
        **signed.get("security", {}),
        "signed": True,
        "signatureAlgorithm": "Ed25519",
        "publicKeyId": private_key["keyId"],
        "publicKey": public_key,
    }
    signature = ed25519_sign(seed, signing_payload(signed))
    signed["security"]["signature"] = b64e(signature)
    return signed


def verify_header_signature(header: dict[str, Any], public_key_doc: dict[str, Any] | None = None) -> bool:
    security = header.get("security", {})
    signature = security.get("signature")
    public_key = public_key_doc.get("publicKey") if public_key_doc else security.get("publicKey")
    if not signature or not public_key:
        return False
    return ed25519_verify(b64d(public_key), signing_payload(header), b64d(signature))


def rotl32(value: int, bits: int) -> int:
    return ((value << bits) & 0xFFFFFFFF) | (value >> (32 - bits))


def quarter_round(state: list[int], a: int, b: int, c: int, d: int) -> None:
    state[a] = (state[a] + state[b]) & 0xFFFFFFFF
    state[d] ^= state[a]
    state[d] = rotl32(state[d], 16)
    state[c] = (state[c] + state[d]) & 0xFFFFFFFF
    state[b] ^= state[c]
    state[b] = rotl32(state[b], 12)
    state[a] = (state[a] + state[b]) & 0xFFFFFFFF
    state[d] ^= state[a]
    state[d] = rotl32(state[d], 8)
    state[c] = (state[c] + state[d]) & 0xFFFFFFFF
    state[b] ^= state[c]
    state[b] = rotl32(state[b], 7)


def chacha20_block(key: bytes, counter: int, nonce: bytes) -> bytes:
    constants = b"expand 32-byte k"
    state = list(struct.unpack("<4I", constants) + struct.unpack("<8I", key) + (counter,) + struct.unpack("<3I", nonce))
    working = state[:]
    for _ in range(10):
        quarter_round(working, 0, 4, 8, 12)
        quarter_round(working, 1, 5, 9, 13)
        quarter_round(working, 2, 6, 10, 14)
        quarter_round(working, 3, 7, 11, 15)
        quarter_round(working, 0, 5, 10, 15)
        quarter_round(working, 1, 6, 11, 12)
        quarter_round(working, 2, 7, 8, 13)
        quarter_round(working, 3, 4, 9, 14)
    return struct.pack("<16I", *[(working[i] + state[i]) & 0xFFFFFFFF for i in range(16)])


def chacha20_xor(key: bytes, nonce: bytes, counter: int, data: bytes) -> bytes:
    output = bytearray()
    for block_index in range(0, len(data), 64):
        keystream = chacha20_block(key, counter + block_index // 64, nonce)
        block = data[block_index : block_index + 64]
        output.extend(a ^ b for a, b in zip(block, keystream))
    return bytes(output)


def clamp_poly_key(r: bytes) -> int:
    value = int.from_bytes(r, "little")
    value &= 0x0FFFFFFC0FFFFFFC0FFFFFFC0FFFFFFF
    return value


def poly1305_mac(key: bytes, message: bytes) -> bytes:
    r = clamp_poly_key(key[:16])
    s = int.from_bytes(key[16:], "little")
    acc = 0
    p = (1 << 130) - 5
    for index in range(0, len(message), 16):
        block = message[index : index + 16]
        n = int.from_bytes(block + b"\x01", "little")
        acc = (acc + n) * r % p
    return ((acc + s) % (1 << 128)).to_bytes(16, "little")


def pad16(data: bytes) -> bytes:
    return b"" if len(data) % 16 == 0 else b"\x00" * (16 - len(data) % 16)


def aead_mac_data(aad: bytes, ciphertext: bytes) -> bytes:
    return aad + pad16(aad) + ciphertext + pad16(ciphertext) + struct.pack("<QQ", len(aad), len(ciphertext))


def chacha20_poly1305_encrypt(key: bytes, nonce: bytes, plaintext: bytes, aad: bytes = b"") -> tuple[bytes, bytes]:
    otk = chacha20_block(key, 0, nonce)[:32]
    ciphertext = chacha20_xor(key, nonce, 1, plaintext)
    tag = poly1305_mac(otk, aead_mac_data(aad, ciphertext))
    return ciphertext, tag


def chacha20_poly1305_decrypt(key: bytes, nonce: bytes, ciphertext: bytes, tag: bytes, aad: bytes = b"") -> bytes:
    otk = chacha20_block(key, 0, nonce)[:32]
    expected = poly1305_mac(otk, aead_mac_data(aad, ciphertext))
    if not hmac.compare_digest(expected, tag):
        raise ValueError("ChaCha20-Poly1305 authentication failed")
    return chacha20_xor(key, nonce, 1, ciphertext)


def derive_passphrase_key(passphrase: str, salt: bytes, iterations: int = 200_000) -> bytes:
    return hashlib.pbkdf2_hmac("sha256", passphrase.encode("utf-8"), salt, iterations, dklen=32)

