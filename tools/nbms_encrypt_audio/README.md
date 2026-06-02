# nbms_encrypt_audio

Encrypt audio payloads inside an NBMS `.nbma` archive.

The manifest remains readable, while each audio file is replaced with an encrypted payload. The header audio hash and security flags are updated.

```text
python tools/nbms_encrypt_audio/nbms_encrypt_audio.py examples/converted_phase4/song.nbmh --passphrase test-passphrase
```

Use the same passphrase in `nbms_probe_player`:

```text
python tools/nbms_probe_player/nbms_probe_player.py examples/converted_phase4/song.nbmh --passphrase test-passphrase --render-wav out.wav
```

---

# nbms_encrypt_audio 日本語版

NBMS `.nbma` archive内のaudio payloadを暗号化します。

manifestは読める状態を保ち、各audio fileをencrypted payloadへ置き換えます。headerのaudio hashとsecurity flagsも更新します。

```text
python tools/nbms_encrypt_audio/nbms_encrypt_audio.py examples/converted_phase4/song.nbmh --passphrase test-passphrase
```

`nbms_probe_player` で再生・renderする場合は同じpassphraseを指定します。

```text
python tools/nbms_probe_player/nbms_probe_player.py examples/converted_phase4/song.nbmh --passphrase test-passphrase --render-wav out.wav
```
