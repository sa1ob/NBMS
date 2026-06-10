# NBMSフォーマット仕様ドラフト 日本語版

Version: draft 0.1

NBMSは、曲情報、譜面データ、音源アーカイブを分離して扱うJSONベースの譜面フォーマット案です。

## 目的

- 従来BMSの2文字ID由来の実質的な音数制限を解消する
- 音源をアーカイブにまとめ、raw音声ファイルを大量に直接扱う非効率を減らす
- 作者および音源制作者の権利情報を保持する
- 初期Editor/Viewerで検証しやすい小さなcore仕様にする
- RANDOM、STOP、BGA、media、特殊レーン、将来ルールのための拡張点を残す

## プロジェクト構成

```text
song.nbmh
audio.nbma
score/
  main.nbmc
```

複数譜面の場合:

```text
song.nbmh
audio.nbma
score/
  normal.nbmc
  hyper.nbmc
  another.nbmc
```

## ヘッダーファイル `.nbmh`

形式: JSON

ヘッダーは曲単位のmetadataと、外部project fileへの参照を持ちます。

主要フィールド:

- `format`: `NBMS`
- `version`: draft version
- `id`: 安定したproject identifier
- `title`
- `artist`
- `genre`
- `bpm`
- `audio.file`: `.nbma` へのパス
- `charts[]`: 譜面参照
- `rights`
- `security`

`charts[]` の各要素は1つの `.nbmc` を参照します。

```json
{
  "id": "hyper",
  "file": "score/hyper.nbmc",
  "mode": "beat-7k",
  "difficulty": 10,
  "levelName": "Hyper",
  "hash": "sha256-...",
  "hashAlgorithm": "sha256-compact-canonical-json"
}
```

## 譜面ファイル `.nbmc`

形式: JSON

譜面ファイルは、レーン、タイミング、ノート、background audio、media event、extension宣言を持ちます。

主要フィールド:

- `format`: `NBMS-CHART`
- `version`
- `chartId`
- `mode`
- `resolution`
- `lanes[]`
- `timing[]`
- `notes[]`
- `backgroundAudio[]`
- `mediaEvents[]`
- `extensions[]`

### Compact JSON layout

draft 0.2以降の `.nbmc` は、JSONのまま `compact-json` layoutを標準保存形式とします。
従来のobject配列形式はreadable-json互換形式として読み込み可能ですが、BMS変換後およびStudio保存後のscoreはcompact-jsonで保存します。

compact-jsonでは、`dictionary` にlane/audio/media IDを集約し、配置情報はtuple配列で保存します。

```json
{
  "format": "NBMS-CHART",
  "version": "0.2.0",
  "encoding": "compact-json",
  "chartId": "main",
  "mode": "beat-7k",
  "resolution": 960,
  "dictionary": {
    "lanes": ["scratch", "key1", "key2"],
    "audio": ["wav_01_kick", "wav_02_snare"],
    "media": ["bmp_01_bga"]
  },
  "timing": [[0, "bpm", 180], [3840, "bar"]],
  "notes": [[0, 1, 0, 0], [960, 2, 0, 1]],
  "backgroundAudio": [[0, 0, 0]],
  "mediaEvents": [[0, 0, "image", 0]]
}
```

tuple定義:

```text
notes: [tick, laneIndex, typeIndex, audioIndex?, durationTicks?, volume?, pan?]
backgroundAudio: [tick, audioIndex, backgroundLaneIndex?, volume?, pan?]
mediaEvents: [tick, mediaIndex, type, layer?]
timing: [tick, type, valueOrDuration?, extra?]
```

## Timing

core timing event:

- `bpm`: 指定tickでのBPM変更
- `bar`: 小節線
- `stop`: 指定tickでの停止時間

例:

```json
{ "tick": 3840, "type": "bpm", "value": 180.0 }
```

```json
{ "tick": 7680, "type": "stop", "durationTicks": 960 }
```

## Notes

tap note例:

```json
{
  "tick": 1920,
  "lane": "key1",
  "type": "tap",
  "audioId": "wav_01_kick"
}
```

hold note例:

```json
{
  "tick": 3840,
  "lane": "key3",
  "type": "hold",
  "audioId": "wav_02_long",
  "durationTicks": 1920
}
```

## Background Audio

自動再生音は、playable noteとは別に `backgroundAudio` として表現します。

```json
{
  "tick": 0,
  "audioId": "wav_aa_bgm",
  "lane": "background1"
}
```

## 音源アーカイブ `.nbma`

物理形式: ZIP

必須entry:

```text
manifest.json
audio/
  sound.flac
```

manifestは `audioId` と格納ファイル、codec metadataを対応付けます。

現在のprototype codec:

- `flac`
- `pcm-wav`
- `ogg-vorbis`
- `mp3`

安定版ではFLACを推奨必須codec候補としていますが、既存BMSパッケージを即時変換せず扱うため、prototypeではOGG Vorbisも許可しています。

## メディアアーカイブ `.nbmg`

物理形式: ZIP

`.nbmg` は任意のメディアコンテナです。BGA、画像、動画、LAYER、POOR、BANNER、STAGEFILE などの視覚素材を `.nbma` とは分離して格納します。メディア素材が存在しない曲では省略できます。

必須entry:

```text
manifest.json
media/
  asset files...
```

manifestは `mediaId` と格納ファイル、type metadataを対応付けます。

```json
{
  "format": "NBMS-MEDIA",
  "version": "0.1.0",
  "entries": [
    {
      "mediaId": "bga_intro",
      "path": "media/bga_intro.mp4",
      "type": "video",
      "mimeType": "video/mp4",
      "hash": "sha256-..."
    }
  ]
}
```

譜面側では `mediaEvents[]` から `mediaId` を参照します。

```json
{
  "tick": 3840,
  "mediaId": "bga_intro",
  "type": "video",
  "layer": 0
}
```

NBMSでは、BMSの `#BMPxx` / `#BGAxx` / `#LAYER` / `#POOR` の2文字IDを内部の正規IDとして使い続けません。インポート時に安定した `mediaId` へ変換し、必要なら `nbms.bmsCompat` metadataに由来情報を保持します。

## パッケージ `.nbmp`

物理形式: ZIP

構成:

```text
package_manifest.json
song.nbmh
audio.nbma
score/
  main.nbmc
```

`.nbmp` は配布用packageです。`.nbmh` の代替ではなく、project fileをまとめるためのものです。

## Hash

譜面hashにはcompact canonical JSONを使います。

```text
sha256-canonical-json
```

譜面を一度内部モデルへ読み込み、sort順、default値省略、null省略を固定したcompact-json正規形を生成します。
そのcompact canonical JSONをUTF-8でエンコードし、SHA-256でhash化します。
このため、readable-jsonとcompact-jsonで同じ譜面を表す場合は同一hashになります。

関連: [../spec/hash_canonicalization.md](../spec/hash_canonicalization.md)

## Security

ヘッダーにはsecurity metadataを持たせます。

- signed / unsigned
- encrypted / unencrypted
- public key metadata
- edit policy

暗号化と署名はまだドラフト項目です。現在公開するEditor/Viewerでは、まずopen projectの対応を優先します。

---

# NBMS Format Specification Draft

Version: draft 0.1

NBMS is a JSON-based chart format with separate project metadata, chart data, and audio archive files.

## Goals

- Remove practical BMS object count limitations caused by legacy two-character identifiers.
- Reduce inefficient raw audio handling by collecting audio into an archive.
- Preserve author and sound-source rights metadata.
- Keep the core format small enough for early Editor/Viewer implementation.
- Leave extension points for RANDOM, STOP, BGA, media, special lanes, and future rules.

## Project Layout

```text
song.nbmh
audio.nbma
score/
  main.nbmc
```

For multiple charts:

```text
song.nbmh
audio.nbma
score/
  normal.nbmc
  hyper.nbmc
  another.nbmc
```

## Header File `.nbmh`

Format: JSON

The header stores song-level metadata and references to external project files.

Required logical fields:

- `format`: `NBMS`
- `version`: draft version
- `id`: stable project identifier
- `title`
- `artist`
- `genre`
- `bpm`
- `audio.file`: path to `.nbma`
- `charts[]`: chart references
- `rights`
- `security`

Each `charts[]` item references one `.nbmc` file:

```json
{
  "id": "hyper",
  "file": "score/hyper.nbmc",
  "mode": "beat-7k",
  "difficulty": 10,
  "levelName": "Hyper",
  "hash": "sha256-...",
  "hashAlgorithm": "sha256-compact-canonical-json"
}
```

## Chart File `.nbmc`

Format: JSON

The chart stores lanes, timing events, notes, background audio events, media events, and extension declarations.

Core fields:

- `format`: `NBMS-CHART`
- `version`
- `chartId`
- `mode`
- `resolution`
- `lanes[]`
- `timing[]`
- `notes[]`
- `backgroundAudio[]`
- `mediaEvents[]`
- `extensions[]`

### Compact JSON Layout

From draft 0.2 onward, `.nbmc` keeps JSON as the file format but uses `compact-json` as the standard saved layout.
The older object-array layout remains readable as a compatibility/readable-json form, while converted BMS scores and Studio saves use compact-json.

compact-json stores lane/audio/media IDs in `dictionary` and stores placement data as tuple arrays.

```json
{
  "format": "NBMS-CHART",
  "version": "0.2.0",
  "encoding": "compact-json",
  "chartId": "main",
  "mode": "beat-7k",
  "resolution": 960,
  "dictionary": {
    "lanes": ["scratch", "key1", "key2"],
    "audio": ["wav_01_kick", "wav_02_snare"],
    "media": ["bmp_01_bga"]
  },
  "timing": [[0, "bpm", 180], [3840, "bar"]],
  "notes": [[0, 1, 0, 0], [960, 2, 0, 1]],
  "backgroundAudio": [[0, 0, 0]],
  "mediaEvents": [[0, 0, "image", 0]]
}
```

Tuple definitions:

```text
notes: [tick, laneIndex, typeIndex, audioIndex?, durationTicks?, volume?, pan?]
backgroundAudio: [tick, audioIndex, backgroundLaneIndex?, volume?, pan?]
mediaEvents: [tick, mediaIndex, type, layer?]
timing: [tick, type, valueOrDuration?, extra?]
```

## Timing

Core timing events:

- `bpm`: BPM change at a tick
- `bar`: measure/bar line marker
- `stop`: stop duration in ticks

Example:

```json
{ "tick": 3840, "type": "bpm", "value": 180.0 }
```

```json
{ "tick": 7680, "type": "stop", "durationTicks": 960 }
```

## Notes

Example tap note:

```json
{
  "tick": 1920,
  "lane": "key1",
  "type": "tap",
  "audioId": "wav_01_kick"
}
```

Example hold note:

```json
{
  "tick": 3840,
  "lane": "key3",
  "type": "hold",
  "audioId": "wav_02_long",
  "durationTicks": 1920
}
```

## Background Audio

Background audio is represented separately from playable notes.

```json
{
  "tick": 0,
  "audioId": "wav_aa_bgm",
  "lane": "background1"
}
```

## Audio Archive `.nbma`

Physical format: ZIP

Required entries:

```text
manifest.json
audio/
  sound.flac
```

The manifest maps `audioId` to stored files and codec metadata.

Current prototype codec handling:

- `flac`
- `pcm-wav`
- `ogg-vorbis`
- `mp3`

FLAC remains the preferred required codec target for the future stable format, but the prototype allows OGG Vorbis to support existing BMS packages without immediate transcoding.

## Media Archive `.nbmg`

Physical format: ZIP

`.nbmg` is an optional media container. It stores visual assets such as BGA, images, videos, LAYER, POOR, BANNER, and STAGEFILE separately from `.nbma`. Songs without media assets may omit it.

Required entries:

```text
manifest.json
media/
  asset files...
```

The manifest maps `mediaId` values to archived files and type metadata.

```json
{
  "format": "NBMS-MEDIA",
  "version": "0.1.0",
  "entries": [
    {
      "mediaId": "bga_intro",
      "path": "media/bga_intro.mp4",
      "type": "video",
      "mimeType": "video/mp4",
      "hash": "sha256-..."
    }
  ]
}
```

Charts reference media assets from `mediaEvents[]`.

```json
{
  "tick": 3840,
  "mediaId": "bga_intro",
  "type": "video",
  "layer": 0
}
```

NBMS does not keep BMS `#BMPxx` / `#BGAxx` / `#LAYER` / `#POOR` two-character IDs as canonical internal IDs. Importers should map them to stable `mediaId` values and preserve source information in `nbms.bmsCompat` metadata when needed.

## Package `.nbmp`

Physical format: ZIP

Package layout:

```text
package_manifest.json
song.nbmh
audio.nbma
score/
  main.nbmc
```

`.nbmp` is a distribution package. It does not replace `.nbmh`; it bundles project files.

## Hashing

Chart hashes use compact canonical JSON:

```text
sha256-compact-canonical-json
```

Players and tools first load a chart into the internal chart model, then generate a compact-json canonical form with stable sorting and fixed default/null omission rules.
That compact canonical JSON is encoded as UTF-8 and hashed with SHA-256.
Readable-json and compact-json representations of the same chart therefore produce the same hash.

See also: [../spec/hash_canonicalization.md](../spec/hash_canonicalization.md)

## Security

The header contains security metadata:

- signed / unsigned
- encrypted / unencrypted
- public key metadata
- edit policy

Encryption and signing are still draft topics. The current public Editor/Viewer should support open projects first.
