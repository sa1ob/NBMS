# nbms_sign

Sign an NBMS header with an author private key.

```text
python tools/nbms_sign/nbms_sign.py examples/converted_phase3/song.nbmh keys/author.private.json
```

By default the header is updated in place. Use `--output` to write a separate header.

---

# nbms_sign 日本語版

作者private keyを使ってNBMS headerへ署名します。

```text
python tools/nbms_sign/nbms_sign.py examples/converted_phase3/song.nbmh keys/author.private.json
```

既定ではheaderを直接更新します。別fileへ書き出す場合は `--output` を使います。
