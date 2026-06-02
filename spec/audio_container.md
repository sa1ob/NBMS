
# NBMS Audio Archive `.nbma` MVP 日本語版

## 1. 決定事項

`NBMS 0.1` では、`.nbma` をZIPベースの音源アーカイブとして定義します。

これは、完全独自コンテナを急いで作らず、実装しやすい既存コンテナを使う方針に沿ったものです。ZIPは実装、検査、パッケージ化が容易で、初期検証に向いています。

将来的には、同じ論理manifestを保ちながら、より高速なbinary containerを定義する可能性があります。

## 2. 拡張子

- File extension: `.nbma`
- Physical format: ZIP archive
- Required manifest path: `manifest.json`
- Required audio directory: `audio/`

## 3. アーカイブ構成

```text
audio.nbma
  manifest.json
  audio/
    kick_001.flac
    snare_001.flac
    bgm_intro.flac
```

アーカイブ内のpathは `/` 区切り、UTF-8名を使います。

## 4. Manifest

`manifest.json` はUTF-8のJSONです。

各 `entries[]` は再生可能な音源assetを1つ定義します。

必須field:

- `audioId`: `.nbmc` から参照される安定ID
- `path`: ZIP内の相対path
- `codec`: 音声codec
- `sampleRate`: source sample rate
- `channels`: channel count
- `durationMs`: duration milliseconds
- `hash`: 格納された音声file bytesのSHA-256 hash

任意field:

- `loopStartMs`
- `loopEndMs`
- `rightsId`
- `encrypted`

## 5. Codec方針

安定版NBMSではFLACを推奨必須codec候補とします。

現在のprototypeでは、既存BMS音源をそのまま扱うため、以下も受け入れます。

- `ogg-vorbis`
- `pcm-wav`
- `mp3`

未対応codecに遭遇したPlayerは、分かりやすいunsupported-codec errorを出して譜面読み込みを失敗させるべきです。

## 6. Hash

音源entryのhashは、ZIP entryから取り出した格納file bytesに対して計算します。

`.nbmh` に記録されるarchive-level hashは、`.nbma` file全体のbytesに対して計算します。

## 7. 暗号化placeholder

Phase 1では暗号化を実装していません。Phase 4では `manifest.json` を読める状態に保ちつつ、音声payloadを暗号化する方向を検討しています。

## 8. 読み込み手順

1. `.nbma` をZIPとして開く
2. `manifest.json` を読む
3. `audioId` からarchive pathへのindexを作る
4. 譜面が参照する全 `audioId` が存在するか検証する
5. 短い音はpreload、長い音はstreamなど実装に応じて扱う
6. 音声をdecodeして再生する

---

# NBMS Audio Archive `.nbma` MVP

## 1. Decision

For `NBMS 0.1`, `.nbma` is defined as a ZIP-based audio archive.

This satisfies the Phase 1 policy of using an existing container when practical. ZIP is easy to implement, inspect, stream partially in many runtimes, and package without designing a binary index format too early.

Later versions may define a faster binary container while preserving the same logical manifest.

## 2. Extension

- File extension: `.nbma`
- Physical format: ZIP archive
- Required manifest path: `manifest.json`
- Required audio directory: `audio/`

## 3. Archive Layout

```text
audio.nbma
  manifest.json
  audio/
    kick_001.flac
    snare_001.flac
    bgm_intro.flac
```

All paths inside the archive use forward slashes and UTF-8 names.

## 4. Manifest

`manifest.json` is a JSON file encoded as UTF-8.

```json
{
  "format": "NBMS-AUDIO",
  "version": "0.1.0",
  "codecRequired": ["flac"],
  "encrypted": false,
  "entries": [
    {
      "audioId": "kick_001",
      "path": "audio/kick_001.flac",
      "codec": "flac",
      "sampleRate": 44100,
      "channels": 2,
      "durationMs": 250,
      "hash": "sha256-...",
      "rightsId": "sound-dev-001"
    }
  ],
  "rights": [
    {
      "id": "sound-dev-001",
      "name": "Sound Developer",
      "role": "sound-source",
      "license": "All rights reserved"
    }
  ]
}
```

## 5. Entry Rules

Each `entries[]` item defines one playable audio asset.

Required fields:

- `audioId`: Stable identifier referenced by `.nbmc`.
- `path`: Relative path inside the ZIP archive.
- `codec`: Audio codec. `flac` is the preferred required codec target for stable NBMS. The prototype also accepts `ogg-vorbis` to preserve common BMS audio files without mandatory transcoding.
- `sampleRate`: Source sample rate.
- `channels`: Channel count.
- `durationMs`: Duration in milliseconds.
- `hash`: SHA-256 hash of the compressed audio file bytes.

Optional fields:

- `loopStartMs`
- `loopEndMs`
- `rightsId`
- `encrypted`

## 6. Codec Policy

NBMS 0.1 requires FLAC decoding.

- Preferred required target: `flac`
- Prototype supported: `ogg-vorbis`, `pcm-wav`, `mp3`
- Optional future candidates: `opus`, `pcm-s16le`

If a player encounters an unsupported optional codec, it must fail the chart load with a clear unsupported-codec error.

The current prototype tools encode WAV PCM16 sources into a simple FLAC subset that uses verbatim subframes. This is a validation bridge, not the final codec recommendation. Production converters and players should use mature FLAC libraries while preserving the same `.nbma` manifest model.

## 7. Hashing

Audio entry hashes are calculated over the exact stored file bytes after extracting from the ZIP entry.

Archive-level hashes in `.nbmh` are calculated over the complete `.nbma` file bytes.

## 8. Encryption Placeholder

Phase 1 did not implement encryption. Phase 4 encrypts audio payload files while keeping `manifest.json` readable.

When `encrypted` is `false`, audio files are normal FLAC files.

When `encrypted` is `true`, each encrypted entry:

- keeps its logical `audioId`
- keeps its logical `codec`
- changes `path` to an encrypted payload path such as `audio/kick_001.flac.enc`
- stores a hash of the encrypted payload bytes
- stores `encryption.algorithm`
- stores a nonce and authentication tag
- stores the original hash for post-decryption diagnostics

The archive-level manifest stores:

- `encrypted: true`
- `encryption.algorithm`
- `encryption.kdf`
- `encryption.salt`
- `encryption.iterations`

The Phase 4 prototype uses `ChaCha20-Poly1305` with a passphrase-derived key via `PBKDF2-HMAC-SHA256`. Production NBMS should prefer platform key storage or public-key wrapped content keys instead of raw passphrase distribution.

## 9. Loading Procedure

1. Open `.nbma` as ZIP.
2. Read `manifest.json`.
3. Build an `audioId` to archive path index.
4. Validate that every chart-referenced `audioId` exists.
5. Preload short sounds and stream long sounds as appropriate.
6. Decode FLAC data for playback.