# nbms_verify

Verify NBMS hashes and header signature.

```text
python tools/nbms_verify/nbms_verify.py examples/converted_phase3/song.nbmh
```

If the header embeds its public key, no extra key file is required.

```text
python tools/nbms_verify/nbms_verify.py examples/converted_phase3/song.nbmh --public-key keys/author.public.json
```

---

# nbms_verify 日本語版

NBMSのhashとheader signatureを検証します。

```text
python tools/nbms_verify/nbms_verify.py examples/converted_phase3/song.nbmh
```

headerにpublic keyが埋め込まれている場合、追加のkey fileは不要です。

```text
python tools/nbms_verify/nbms_verify.py examples/converted_phase3/song.nbmh --public-key keys/author.public.json
```
