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

## BMS import文字コード/拡張子ポリシー v0.1

NBMS Studioが既存BMSをimportする場合、入力BMSは過去実装との互換性のため文字コードと拡張子を柔軟に扱います。NBMSへ変換した後の `.nbmh` / `.nbmc` は、常にUTF-8 JSONとして保存します。

### 対応拡張子

| extension | 扱い |
| --- | --- |
| `.bms` | 標準BMS。mode推定はheader/channelから行います。 |
| `.bme` | 7keys/14keys系のヒント。最終判定はchannelから行います。 |
| `.bml` | long note系のヒント。最終判定は `#LNTYPE`, `#LNOBJ`, LN channelから行います。 |
| `.pms` | PMS/9keys系のヒント。将来のlane schema拡張で正式対応します。 |

拡張子はmode確定値ではなく、あくまで推定材料です。Importerは `#PLAYER`、使用channel、LN/BGA/eventの有無を優先してchart modeを決めます。

### 読み込み文字コード優先順

1. BOM付き `UTF-8`, `UTF-16LE`, `UTF-16BE`。
2. ユーザーが変換UI/APIで明示したencoding。
3. BMS本文中の `#CHARSET UTF-8` / `#CHARSET SHIFT-JIS` / `#CHARSET EUC-KR`。
4. BOMなしUTF-8として厳密decodeできる場合はUTF-8。
5. `Shift_JIS` fallback。

`#CHARSET` よりBOMを優先します。`#CHARSET` と実際のdecode結果に矛盾がある場合は、変換warningとして `nbms.bmsCompat` に保存します。

### `nbms.bmsCompat` へ保存する情報

Importerは、譜面側metadataに以下の情報を保存します。

```json
{
  "metadata": {
    "bmsCompat": {
      "sourceFormat": "bme",
      "sourceExtension": ".bme",
      "sourceEncoding": "shift_jis",
      "charsetDirective": "SHIFT-JIS",
      "encodingDetection": "charset",
      "encodingWarnings": []
    }
  }
}
```

`sourceEncoding` は実際にdecodeに使ったencoding名です。`encodingDetection` は `bom`, `user`, `charset`, `utf8Strict`, `fallback` のいずれかを推奨値とします。

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
  "mediaEvents": [[0, 0, "bga", 0]]
}
```

tuple定義:

```text
notes: [tick, laneIndex, typeIndex, audioIndex?, durationTicks?, volume?, pan?]
backgroundAudio: [tick, audioIndex, backgroundLaneIndex?, volume?, pan?]
mediaEvents: [tick, mediaIndex, type, layer?, extra?]
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

### `nbms.longNote` Long Note Semantics v0.1

NBMS coreでは、長いnoteの物理的な長さは `durationTicks` を持つnoteで表現します。標準core typeは `tap`, `hold`, `mine` です。`hold` は最小互換のLNとして扱い、CN/HCNなどの判定差は `nbms.longNote` 拡張で表現します。

```json
{
  "tick": 3840,
  "lane": "key3",
  "type": "hold",
  "audioId": "wav_02_long_start",
  "durationTicks": 1920,
  "endAudioId": "wav_02_long_end",
  "longNoteMode": "ln"
}
```

`endAudioId` と `longNoteMode` は `nbms.longNote` を宣言したchartで使える任意フィールドです。未対応readerは `type: "hold"` と `durationTicks` だけで最小再生できます。

`metadata.extensions["nbms.longNote"]` には、chart全体の解釈とBMS由来情報を保存します。

```json
{
  "extensions": [
    { "id": "nbms.longNote", "version": "0.1.0", "required": false }
  ],
  "metadata": {
    "extensions": {
      "nbms.longNote": {
        "version": "0.1.0",
        "defaultMode": "ln",
        "modes": ["ln", "cn", "hcn"],
        "bmsCompat": {
          "sourceLnType": 1,
          "sourceLnObj": "ZZ",
          "sourceLnMode": 1
        }
      }
    }
  }
}
```

| field | hash対象 | 役割 |
| --- | --- | --- |
| `note.type` | Yes | coreは `hold`。将来 `cn` / `hcn` を直接type化する場合もhash対象。 |
| `note.durationTicks` | Yes | LN長さの正規値。0以下は不正。 |
| `note.audioId` | Yes | 始点音。 |
| `note.endAudioId` | Yes | 終端音。省略可。 |
| `note.longNoteMode` | Yes | `ln`, `cn`, `hcn`。省略時は拡張payloadの `defaultMode`。 |
| `metadata.extensions.nbms.longNote.defaultMode` | Yes | noteごとに未指定の場合の既定LN種別。 |
| `metadata.extensions.nbms.longNote.bmsCompat` | No | `#LNTYPE`, `#LNOBJ`, `#LNMODE` 由来の変換根拠。 |

Validator issue code候補:

| code | severity | target reference | 意味 |
| --- | --- | --- | --- |
| `NBMS_LN_DURATION_INVALID` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | LN noteの `durationTicks` が0以下です。 |
| `NBMS_LN_END_BEFORE_START` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | 終端tickが始点tick以前です。 |
| `NBMS_LN_OVERLAP_SAME_LANE` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | 同一laneでLN同士またはLNとnoteが重なっています。 |
| `NBMS_LN_END_AUDIO_REF_MISSING` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}:audio:{audioId}` | `endAudioId` が `.nbma` に存在しません。 |
| `NBMS_LN_MODE_UNKNOWN` | Warning | `chart:{chartId}:tick:{tick}:lane:{lane}` | 未知の `longNoteMode` です。 |
| `NBMS_LN_BMS_COMPAT_UNRESOLVED` | Warning | `chart:{chartId}` | BMS由来のLN情報が完全にはcore noteへ変換できていません。 |

## Background Audio

自動再生音は、playable noteとは別に `backgroundAudio` として表現します。

```json
{
  "tick": 0,
  "audioId": "wav_aa_bgm",
  "lane": "background1"
}
```

## `nbms.editor` Editor State v0.1

`nbms.editor` は、NBMS StudioなどのEditorが使う表示・編集状態を保持する任意拡張です。再生、判定、音声発音、ランキングに影響しないため、譜面ハッシュ対象外です。

### 保存位置

`extensions[]` は拡張の宣言だけに使います。Editor固有payloadは、譜面ファイルの `metadata.extensions["nbms.editor"]` に保存します。

```json
{
  "extensions": [
    { "id": "nbms.editor", "version": "0.1.0", "required": false }
  ],
  "metadata": {
    "extensions": {
      "nbms.editor": {
        "version": "0.1.0",
        "shared": {
          "grid": { "division": 16, "snapEnabled": true },
          "visibleLaneGroups": ["events", "playable", "backgroundAudio", "media"],
          "backgroundAudioLaneCount": 16
        },
        "local": {
          "viewStartTick": 0,
          "selectedPanel": "chart",
          "assetTab": "audio",
          "collapsedLaneGroups": []
        }
      }
    }
  }
}
```

### フィールド

| field | hash対象 | 役割 |
| --- | --- | --- |
| `version` | No | `nbms.editor` payloadの版。 |
| `shared.grid.division` | No | 1小節あたりの表示分割数。例: `16`, `32`, `48`, `64`。 |
| `shared.grid.snapEnabled` | No | grid吸着の既定状態。 |
| `shared.visibleLaneGroups` | No | project共有してよい表示lane group。 |
| `shared.backgroundAudioLaneCount` | No | BGM/background lane表示数。 |
| `local.viewStartTick` | No | 開いた時の表示開始tick。 |
| `local.selectedPanel` | No | `chart`, `events`, `assetsAudio`, `assetsMedia` などの最後の表示panel。 |
| `local.assetTab` | No | asset listの最後のtab。 |
| `local.collapsedLaneGroups` | No | 折りたたみ中lane group。 |

### 保存方針

- `nbms.editor` は未知フィールドを破壊せずroundtripすることを推奨します。
- Studioは既定では `shared` を保存できます。
- `local` はユーザー環境依存のため、Studio設定で「Editor状態を譜面に保存する」を有効にした場合だけ保存します。
- Viewerは `nbms.editor` を無視します。

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

### `nbms.visual` media event state v0.2

`nbms.visual` は、`.nbmg` に格納された画像/動画assetを譜面時間上でどのlayerに表示するかを定義します。

譜面ファイル上の `mediaEvents[]` はイベント列であり、Viewerは再生時にこれをlayerごとの状態へ展開します。

```json
{
  "tick": 3840,
  "type": "bga",
  "mediaId": "bga_intro",
  "layer": 0,
  "order": 0,
  "startOffsetMs": 0,
  "playbackRate": 1.0,
  "loop": false,
  "endBehavior": "holdLastFrame",
  "opacity": 1.0,
  "blend": "normal"
}
```

主要フィールド:

- `type`: `bga`, `layer`, `poor`, `clear`, `banner`, `stagefile`, `preview`。prototype互換として `image` / `video` を読み込んだ場合は `bga` として扱います。
- `mediaId`: `.nbmg` manifestの `mediaId`。`clear` では省略します。
- `layer`: 合成layer番号。省略時は `bga` / `poor` が `0`、`layer` が `1` です。
- `order`: 同tick/同layerのイベント順序を安定させるための任意整数。BMS import時の元順序を保持できます。
- `startOffsetMs`: 動画を途中位置から開始する場合のoffset。省略時は `0`。
- `playbackRate`: 動画asset自体の再生倍率。省略時は `1.0`。通常のViewer Hi-Speedや将来の譜面/音声再生速度変更には追従しません。
- `loop`: 動画をloopするか。省略時は `false`。
- `endBehavior`: 動画終了後の表示。既定は `holdLastFrame`。
- `opacity`: layer透明度。省略時は `1.0`。
- `blend`: `normal` を既定とします。加算合成などは将来拡張です。

#### Event Type

| type | 役割 | 既定layer | mediaId |
| --- | --- | ---: | --- |
| `bga` | 基本BGA。BMS `#xxx04` 由来。 | `0` | 必須 |
| `layer` | BGA上の重ね合わせ。BMS `#xxx07` 由来。 | `1` | 必須 |
| `poor` | POOR/miss visual。previewではtimeline表示、gameplayではmiss時triggerにできる。 | `0` | 必須 |
| `clear` | 指定layerまたは全layerを消す。 | 省略時は全layer | 省略 |
| `stagefile` | 曲選択/読込用画像。timeline合成対象外。 | - | 必須 |
| `banner` | 曲一覧banner。timeline合成対象外。 | - | 必須 |
| `preview` | 曲選択用preview media。timeline合成対象外。 | - | 必須 |

#### Default値

| field | default | 備考 |
| --- | --- | --- |
| `layer` | `bga` / `poor` は `0`、`layer` は `1`、global `clear` は省略 | layer番号が大きいほど上に描画します。 |
| `order` | `0` | 同tick/同layer/typeの安定順序に使います。 |
| `startOffsetMs` | `0` | 動画assetで使用します。 |
| `playbackRate` | `1.0` | Viewer Hi-Speedや練習再生速度へ暗黙追従しません。 |
| `loop` | `false` | 動画assetで使用します。 |
| `endBehavior` | `holdLastFrame` | 消したい場合は明示的に `clear` を置きます。 |
| `opacity` | `1.0` | 範囲は `0.0` から `1.0`。 |
| `blend` | `normal` | その他の合成は将来拡張です。 |

compact-jsonでは5番目の `extra` objectに `order`, `startOffsetMs`, `playbackRate`, `loop`, `endBehavior`, `opacity`, `blend` を格納できます。省略時は上記の既定値を使います。

Viewerの再生時状態は、概念的には以下のように扱います。

```json
{
  "layers": {
    "0": {
      "mediaId": "bga_intro",
      "kind": "video",
      "eventType": "bga",
      "startedAtTick": 3840,
      "startedAtSeconds": 12.5,
      "startOffsetMs": 0,
      "playbackRate": 1.0,
      "loop": false,
      "endBehavior": "holdLastFrame",
      "opacity": 1.0,
      "blend": "normal"
    }
  }
}
```

同tickに複数の `mediaEvents[]` が存在する場合、Viewer/Editorは以下の順で適用します。

1. `tick` 昇順
2. `layer` 昇順。`clear` で `layer` が省略された場合は全layer対象として先に適用
3. `type` 優先順: `clear`, `bga`, `layer`, `poor`, `banner`, `stagefile`, `preview`
4. `order` 昇順
5. `mediaId` のordinal昇順

`clear` は `mediaId` を持たないイベントです。`layer` が指定された場合はそのlayerだけを消し、`layer` が省略された場合は全layerを消します。

動画終了後の既定動作は `holdLastFrame` です。これにより次のmedia eventまで黒画面化しにくくなります。明示的に消したい場合は後続tickに `clear` を置きます。将来拡張として `clear`, `transparent`, `black` を許可できます。

動画BGAの再生クロックは、原則として動画decoderの通常速度を基準にします。ViewerのHi-Speedは譜面表示密度だけを変えるため動画速度へ影響しません。将来、練習再生などで譜面/音声の再生速度を変更する場合も、動画BGAは自動的には追従せず、開始/シーク時に `mediaEvent` 発生時刻との差分から表示位置だけを合わせます。動画自体を意図的に変速したい場合のみ、譜面データ側で `playbackRate` を明示します。

visual validatorのissue code候補:

| code | severity | target reference | 意味 |
| --- | --- | --- | --- |
| `NBMS_VISUAL_MEDIA_REF_MISSING` | Error | `chart:{chartId}:media:{mediaId}` | `mediaEvents[]` が存在しない `.nbmg` entryを参照しています。 |
| `NBMS_VISUAL_TYPE_UNKNOWN` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | 未知のevent typeです。 |
| `NBMS_VISUAL_LAYER_INVALID` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | viewer/editorの対応範囲外layerです。 |
| `NBMS_VISUAL_CLEAR_WITH_MEDIA` | Warning | `chart:{chartId}:tick:{tick}` | `clear` eventに `mediaId` が付いています。readerは無視できます。 |
| `NBMS_VISUAL_MEDIA_KIND_UNSUPPORTED` | Warning | `media:{mediaId}` | `.nbmg` entryは存在するがcodec/kindをdecodeできません。 |

NBMSでは、BMSの `#BMPxx` / `#BGAxx` / `#LAYER` / `#POOR` の2文字IDを内部の正規IDとして使い続けません。インポート時に安定した `mediaId` へ変換し、必要なら `nbms.bmsCompat` metadataに由来情報を保持します。

## Validator Issue Model v0.1

NBMS toolは、package検査、参照切れ検査、譜面構造検査を同じissue modelで扱います。

```json
{
  "severity": "Error",
  "code": "NBMS_AUDIO_REF_MISSING",
  "source": "main",
  "targetReference": "chart:main:tick:1920:lane:key1",
  "message": "missing audio reference: wav_01",
  "autoFix": "repairReference"
}
```

### severity

| severity | 意味 |
| --- | --- |
| `Error` | package作成、再生、変換結果の正当性に影響する問題。 |
| `Warning` | 再生できる可能性はあるが、互換性、欠落、品質低下の危険がある問題。 |
| `Info` | 制作者への通知。直ちに修正不要。 |

### target reference

| pattern | 対象 |
| --- | --- |
| `header` | `.nbmh` 全体 |
| `header.audio` | headerのaudio参照 |
| `header.media` | headerのmedia参照 |
| `chart:{chartId}` | chart全体 |
| `chart:{chartId}:tick:{tick}` | tick位置 |
| `chart:{chartId}:tick:{tick}:lane:{lane}` | tick/lane位置 |
| `chart:{chartId}:note:{index}` | note配列index |
| `chart:{chartId}:backgroundAudio:{index}` | backgroundAudio配列index |
| `chart:{chartId}:mediaEvent:{index}` | mediaEvents配列index |
| `audio:{audioId}` | audio manifest entry |
| `media:{mediaId}` | media manifest entry |
| `package:{path}` | package内file path |

### issue code

| code | severity | autoFix | 意味 |
| --- | --- | --- | --- |
| `PKG_HEADER_MISSING` | Error | none | header fileが存在しません。 |
| `PKG_AUDIO_REF_MISSING` | Error | none | headerのaudio参照がありません。 |
| `PKG_AUDIO_FILE_MISSING` | Error | none | `.nbma` fileが存在しません。 |
| `PKG_AUDIO_HASH_MISMATCH` | Error | recomputeHash | headerのaudio hashと実file hashが一致しません。 |
| `PKG_MEDIA_FILE_MISSING` | Error/Warning | none | `.nbmg` fileが存在しません。optional mediaならWarning。 |
| `PKG_MEDIA_HASH_MISMATCH` | Error | recomputeHash | headerのmedia hashと実file hashが一致しません。 |
| `PKG_CHART_FILE_MISSING` | Error | none | chart fileが存在しません。 |
| `PKG_CHART_HASH_MISMATCH` | Error | recomputeHash | headerのchart hashと実chart hashが一致しません。 |
| `PKG_AUDIO_ARCHIVE_ENTRY_INVALID` | Error | none | `.nbma` 内entryが欠落、破損、hash不一致です。 |
| `PKG_MEDIA_ARCHIVE_ENTRY_INVALID` | Error | none | `.nbmg` 内entryが欠落、破損、hash不一致です。 |
| `NBMS_AUDIO_REF_MISSING` | Error | repairReference | note/backgroundAudioが存在しないaudioを参照しています。 |
| `NBMS_MEDIA_REF_MISSING` | Error | repairReference | mediaEventsが存在しないmediaを参照しています。 |
| `NBMS_ASSET_UNUSED` | Warning | removeUnused | 未使用assetです。 |
| `NBMS_AUDIO_DUPLICATE` | Warning | mergeDuplicate | 内容重複のaudio assetです。 |
| `NBMS_MEDIA_DUPLICATE` | Warning | mergeDuplicate | 内容重複のmedia assetです。 |
| `NBMS_NOTE_OVERLAP` | Warning/Error | none | 同一lane上でnoteが重なっています。 |
| `NBMS_NOTE_HORIZONTAL_DUPLICATE` | Info | none | 同tick上に多数の同時noteがあります。 |

`autoFix` は `none`, `repairReference`, `removeUnused`, `mergeDuplicate`, `recomputeHash` のいずれかを推奨値とします。未知の `autoFix` は `none` として扱います。

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

## BMS Import Encoding and Extension Policy v0.1

When NBMS Studio imports existing BMS files, it accepts legacy encoding and extension differences for compatibility. Converted `.nbmh` / `.nbmc` files are always saved as UTF-8 JSON.

### Supported Input Extensions

| extension | handling |
| --- | --- |
| `.bms` | Standard BMS. Mode is inferred from header/channel data. |
| `.bme` | Hint for 7keys/14keys charts. Channels remain authoritative. |
| `.bml` | Hint for long-note charts. `#LNTYPE`, `#LNOBJ`, and LN channels remain authoritative. |
| `.pms` | Hint for PMS/9keys charts. Formal support depends on future lane schema extensions. |

The extension is only a hint. Importers determine chart mode primarily from `#PLAYER`, used channels, and LN/BGA/event data.

### Encoding Priority

1. BOM-marked `UTF-8`, `UTF-16LE`, or `UTF-16BE`.
2. Encoding explicitly selected by the user through UI/API.
3. `#CHARSET UTF-8`, `#CHARSET SHIFT-JIS`, or `#CHARSET EUC-KR`.
4. BOM-less UTF-8 if the file strictly decodes as UTF-8.
5. `Shift_JIS` fallback.

BOM takes precedence over `#CHARSET`. If `#CHARSET` conflicts with the actual decoding result, importers store a conversion warning in `nbms.bmsCompat`.

### Metadata Stored in `nbms.bmsCompat`

Importers store encoding/source information in chart metadata.

```json
{
  "metadata": {
    "bmsCompat": {
      "sourceFormat": "bme",
      "sourceExtension": ".bme",
      "sourceEncoding": "shift_jis",
      "charsetDirective": "SHIFT-JIS",
      "encodingDetection": "charset",
      "encodingWarnings": []
    }
  }
}
```

`sourceEncoding` is the encoding actually used for decoding. Recommended `encodingDetection` values are `bom`, `user`, `charset`, `utf8Strict`, and `fallback`.

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
  "mediaEvents": [[0, 0, "bga", 0]]
}
```

Tuple definitions:

```text
notes: [tick, laneIndex, typeIndex, audioIndex?, durationTicks?, volume?, pan?]
backgroundAudio: [tick, audioIndex, backgroundLaneIndex?, volume?, pan?]
mediaEvents: [tick, mediaIndex, type, layer?, extra?]
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

### `nbms.longNote` Long Note Semantics v0.1

In NBMS core, the physical length of a long note is represented by a note with `durationTicks`. The required core note types are `tap`, `hold`, and `mine`. `hold` is the minimum compatible LN representation, while CN/HCN judgement differences are expressed through the optional `nbms.longNote` extension.

```json
{
  "tick": 3840,
  "lane": "key3",
  "type": "hold",
  "audioId": "wav_02_long_start",
  "durationTicks": 1920,
  "endAudioId": "wav_02_long_end",
  "longNoteMode": "ln"
}
```

`endAudioId` and `longNoteMode` are optional fields available when the chart declares `nbms.longNote`. Readers that do not support the extension can still use `type: "hold"` and `durationTicks` for minimum playback.

`metadata.extensions["nbms.longNote"]` stores chart-wide interpretation and BMS source information.

```json
{
  "extensions": [
    { "id": "nbms.longNote", "version": "0.1.0", "required": false }
  ],
  "metadata": {
    "extensions": {
      "nbms.longNote": {
        "version": "0.1.0",
        "defaultMode": "ln",
        "modes": ["ln", "cn", "hcn"],
        "bmsCompat": {
          "sourceLnType": 1,
          "sourceLnObj": "ZZ",
          "sourceLnMode": 1
        }
      }
    }
  }
}
```

| field | hash target | purpose |
| --- | --- | --- |
| `note.type` | Yes | Core uses `hold`. Future direct `cn` / `hcn` types are also hash-relevant. |
| `note.durationTicks` | Yes | Canonical LN length. Values less than or equal to zero are invalid. |
| `note.audioId` | Yes | Start sound. |
| `note.endAudioId` | Yes | End sound. Optional. |
| `note.longNoteMode` | Yes | `ln`, `cn`, or `hcn`. Defaults to extension payload `defaultMode`. |
| `metadata.extensions.nbms.longNote.defaultMode` | Yes | Default LN mode when a note omits `longNoteMode`. |
| `metadata.extensions.nbms.longNote.bmsCompat` | No | Conversion evidence from `#LNTYPE`, `#LNOBJ`, and `#LNMODE`. |

Suggested validator issue codes:

| code | severity | target reference | meaning |
| --- | --- | --- | --- |
| `NBMS_LN_DURATION_INVALID` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | LN note has `durationTicks` less than or equal to zero. |
| `NBMS_LN_END_BEFORE_START` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | End tick is before or equal to the start tick. |
| `NBMS_LN_OVERLAP_SAME_LANE` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}` | LN overlaps another note or LN on the same lane. |
| `NBMS_LN_END_AUDIO_REF_MISSING` | Error | `chart:{chartId}:tick:{tick}:lane:{lane}:audio:{audioId}` | `endAudioId` does not exist in `.nbma`. |
| `NBMS_LN_MODE_UNKNOWN` | Warning | `chart:{chartId}:tick:{tick}:lane:{lane}` | Unknown `longNoteMode`. |
| `NBMS_LN_BMS_COMPAT_UNRESOLVED` | Warning | `chart:{chartId}` | BMS-derived LN data could not be fully converted into core notes. |

## Background Audio

Background audio is represented separately from playable notes.

```json
{
  "tick": 0,
  "audioId": "wav_aa_bgm",
  "lane": "background1"
}
```

## `nbms.editor` Editor State v0.1

`nbms.editor` is an optional extension for editor-specific view and editing state used by tools such as NBMS Studio. It does not affect playback, judgement, audio triggering, or rankings, so it is excluded from chart hashes.

### Storage

`extensions[]` is only an extension declaration list. The editor-specific payload is stored in `metadata.extensions["nbms.editor"]` in the chart file.

```json
{
  "extensions": [
    { "id": "nbms.editor", "version": "0.1.0", "required": false }
  ],
  "metadata": {
    "extensions": {
      "nbms.editor": {
        "version": "0.1.0",
        "shared": {
          "grid": { "division": 16, "snapEnabled": true },
          "visibleLaneGroups": ["events", "playable", "backgroundAudio", "media"],
          "backgroundAudioLaneCount": 16
        },
        "local": {
          "viewStartTick": 0,
          "selectedPanel": "chart",
          "assetTab": "audio",
          "collapsedLaneGroups": []
        }
      }
    }
  }
}
```

### Fields

| field | hash target | purpose |
| --- | --- | --- |
| `version` | No | Payload version for `nbms.editor`. |
| `shared.grid.division` | No | Display grid division per measure, for example `16`, `32`, `48`, or `64`. |
| `shared.grid.snapEnabled` | No | Default grid snapping state. |
| `shared.visibleLaneGroups` | No | Lane groups that are safe to share in the project. |
| `shared.backgroundAudioLaneCount` | No | Display count for BGM/background lanes. |
| `local.viewStartTick` | No | Initial view start tick when reopening the chart. |
| `local.selectedPanel` | No | Last selected panel such as `chart`, `events`, `assetsAudio`, or `assetsMedia`. |
| `local.assetTab` | No | Last selected asset tab. |
| `local.collapsedLaneGroups` | No | Collapsed lane groups. |

### Save Policy

- Tools should roundtrip unknown `nbms.editor` fields without destroying them.
- Studio may save `shared` by default.
- `local` is user-environment-specific and should only be saved when the user enables "save editor state into chart".
- Viewers ignore `nbms.editor`.

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
  "type": "bga",
  "layer": 0
}
```

### `nbms.visual` Media Event State v0.2

`nbms.visual` defines which image/video asset from `.nbmg` is displayed on which visual layer at chart time.

Chart `mediaEvents[]` is an event stream. Players expand that stream into per-layer state during playback.

```json
{
  "tick": 3840,
  "type": "bga",
  "mediaId": "bga_intro",
  "layer": 0,
  "order": 0,
  "startOffsetMs": 0,
  "playbackRate": 1.0,
  "loop": false,
  "endBehavior": "holdLastFrame",
  "opacity": 1.0,
  "blend": "normal"
}
```

Fields:

- `type`: `bga`, `layer`, `poor`, `clear`, `banner`, `stagefile`, or `preview`. For prototype compatibility, readers treat `image` / `video` as `bga`.
- `mediaId`: `.nbmg` manifest `mediaId`. Omitted for `clear`.
- `layer`: composition layer. Defaults are `0` for `bga` / `poor`, and `1` for `layer`.
- `order`: optional stable event order at the same tick/layer. Importers may use it to preserve BMS source order.
- `startOffsetMs`: video start offset. Default is `0`.
- `playbackRate`: playback rate for the video asset itself. Default is `1.0`. Viewer Hi-Speed and future chart/audio playback speed changes do not implicitly change it.
- `loop`: whether the video loops. Default is `false`.
- `endBehavior`: video end behavior. Default is `holdLastFrame`.
- `opacity`: layer opacity. Default is `1.0`.
- `blend`: default is `normal`; additive blending and other modes are future extensions.

#### Event Types

| type | role | default layer | mediaId |
| --- | --- | ---: | --- |
| `bga` | Base BGA. Imported from BMS `#xxx04`. | `0` | required |
| `layer` | Overlay above BGA. Imported from BMS `#xxx07`. | `1` | required |
| `poor` | POOR/miss visual. Preview viewers may show it on the timeline; gameplay viewers may trigger it on miss. | `0` | required |
| `clear` | Clears one layer or all layers. | all layers when omitted | omitted |
| `stagefile` | Song-select/loading image. Not timeline-composited. | - | required |
| `banner` | Song-list banner. Not timeline-composited. | - | required |
| `preview` | Song-select preview media. Not timeline-composited. | - | required |

#### Defaults

| field | default | notes |
| --- | --- | --- |
| `layer` | `0` for `bga` / `poor`, `1` for `layer`, omitted for global `clear` | Larger layers draw above smaller layers. |
| `order` | `0` | Stabilizes same-tick/same-layer/same-type ordering. |
| `startOffsetMs` | `0` | Applies to video assets. |
| `playbackRate` | `1.0` | Does not follow Viewer Hi-Speed or future practice playback speed. |
| `loop` | `false` | Applies to video assets. |
| `endBehavior` | `holdLastFrame` | Place an explicit `clear` event when the layer should disappear. |
| `opacity` | `1.0` | Valid range is `0.0` to `1.0`. |
| `blend` | `normal` | Other blend modes are future extensions. |

In compact-json, `mediaEvents` may store these optional fields in the fifth `extra` object:

```json
[[3840, 0, "bga", 0, { "order": 0, "playbackRate": 1.0, "endBehavior": "holdLastFrame" }]]
```

Conceptual playback state:

```json
{
  "layers": {
    "0": {
      "mediaId": "bga_intro",
      "kind": "video",
      "eventType": "bga",
      "startedAtTick": 3840,
      "startedAtSeconds": 12.5,
      "startOffsetMs": 0,
      "playbackRate": 1.0,
      "loop": false,
      "endBehavior": "holdLastFrame",
      "opacity": 1.0,
      "blend": "normal"
    }
  }
}
```

When multiple `mediaEvents[]` exist at the same tick, players/editors apply them in this stable order:

1. `tick` ascending.
2. `layer` ascending. A `clear` event without `layer` targets all layers and is applied before layer-specific events at the same tick.
3. `type` priority: `clear`, `bga`, `layer`, `poor`, `banner`, `stagefile`, `preview`.
4. `order` ascending.
5. `mediaId` ordinal ascending.

`clear` has no `mediaId`. With `layer`, it clears only that layer. Without `layer`, it clears all layers.

The default video end behavior is `holdLastFrame`, which avoids unexpected black frames until the next media event. Authors can place a later `clear` event when the layer should disappear. Future extensions may allow explicit `clear`, `transparent`, or `black` end behaviors.

Video BGA playback uses the decoder's normal media clock by default. Viewer Hi-Speed changes chart visual spacing only and does not affect video speed. If a future practice mode changes chart/audio playback speed, video BGA still does not automatically follow that speed; players only align the visible position on start/seek from the difference between the start time and the media event time. Authors should set `playbackRate` only when the video asset itself is intentionally meant to run at a different speed.

Suggested visual validator issue codes:

| code | severity | target reference | meaning |
| --- | --- | --- | --- |
| `NBMS_VISUAL_MEDIA_REF_MISSING` | Error | `chart:{chartId}:media:{mediaId}` | `mediaEvents[]` references a missing `.nbmg` entry. |
| `NBMS_VISUAL_TYPE_UNKNOWN` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | Unknown event type. |
| `NBMS_VISUAL_LAYER_INVALID` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | Layer is outside the supported viewer/editor range. |
| `NBMS_VISUAL_CLEAR_WITH_MEDIA` | Warning | `chart:{chartId}:tick:{tick}` | `clear` event has a `mediaId`; readers may ignore it. |
| `NBMS_VISUAL_MEDIA_KIND_UNSUPPORTED` | Warning | `media:{mediaId}` | `.nbmg` entry exists, but its kind/codec cannot be decoded. |

NBMS does not keep BMS `#BMPxx` / `#BGAxx` / `#LAYER` / `#POOR` two-character IDs as canonical internal IDs. Importers should map them to stable `mediaId` values and preserve source information in `nbms.bmsCompat` metadata when needed.

## Validator Issue Model v0.1

NBMS tools use one issue model for package validation, missing-reference checks, and chart-structure validation.

```json
{
  "severity": "Error",
  "code": "NBMS_AUDIO_REF_MISSING",
  "source": "main",
  "targetReference": "chart:main:tick:1920:lane:key1",
  "message": "missing audio reference: wav_01",
  "autoFix": "repairReference"
}
```

### severity

| severity | meaning |
| --- | --- |
| `Error` | Affects package creation, playback, or conversion validity. |
| `Warning` | Playback may still work, but compatibility, missing data, or quality loss is likely. |
| `Info` | Author-facing notice. Immediate action is not required. |

### target reference

| pattern | target |
| --- | --- |
| `header` | Whole `.nbmh` file |
| `header.audio` | Header audio reference |
| `header.media` | Header media reference |
| `chart:{chartId}` | Whole chart |
| `chart:{chartId}:tick:{tick}` | Tick position |
| `chart:{chartId}:tick:{tick}:lane:{lane}` | Tick/lane position |
| `chart:{chartId}:note:{index}` | `notes[]` index |
| `chart:{chartId}:backgroundAudio:{index}` | `backgroundAudio[]` index |
| `chart:{chartId}:mediaEvent:{index}` | `mediaEvents[]` index |
| `audio:{audioId}` | Audio manifest entry |
| `media:{mediaId}` | Media manifest entry |
| `package:{path}` | File path inside a package |

### issue code

| code | severity | autoFix | meaning |
| --- | --- | --- | --- |
| `PKG_HEADER_MISSING` | Error | none | Header file is missing. |
| `PKG_AUDIO_REF_MISSING` | Error | none | Header audio reference is missing. |
| `PKG_AUDIO_FILE_MISSING` | Error | none | `.nbma` file is missing. |
| `PKG_AUDIO_HASH_MISMATCH` | Error | recomputeHash | Header audio hash and actual file hash differ. |
| `PKG_MEDIA_FILE_MISSING` | Error/Warning | none | `.nbmg` file is missing. Optional media may be Warning. |
| `PKG_MEDIA_HASH_MISMATCH` | Error | recomputeHash | Header media hash and actual file hash differ. |
| `PKG_CHART_FILE_MISSING` | Error | none | Chart file is missing. |
| `PKG_CHART_HASH_MISMATCH` | Error | recomputeHash | Header chart hash and actual chart hash differ. |
| `PKG_AUDIO_ARCHIVE_ENTRY_INVALID` | Error | none | `.nbma` entry is missing, broken, or hash-mismatched. |
| `PKG_MEDIA_ARCHIVE_ENTRY_INVALID` | Error | none | `.nbmg` entry is missing, broken, or hash-mismatched. |
| `NBMS_AUDIO_REF_MISSING` | Error | repairReference | Note/backgroundAudio references missing audio. |
| `NBMS_MEDIA_REF_MISSING` | Error | repairReference | mediaEvents reference missing media. |
| `NBMS_ASSET_UNUSED` | Warning | removeUnused | Unused asset. |
| `NBMS_AUDIO_DUPLICATE` | Warning | mergeDuplicate | Duplicate audio asset content. |
| `NBMS_MEDIA_DUPLICATE` | Warning | mergeDuplicate | Duplicate media asset content. |
| `NBMS_NOTE_OVERLAP` | Warning/Error | none | Notes overlap on the same lane. |
| `NBMS_NOTE_HORIZONTAL_DUPLICATE` | Info | none | Many notes exist at the same tick. |

Recommended `autoFix` values are `none`, `repairReference`, `removeUnused`, `mergeDuplicate`, and `recomputeHash`. Unknown `autoFix` values are treated as `none`.

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

NBMS separates hashes by purpose.

| hash | purpose |
| --- | --- |
| `chartHash` | Package consistency and chart-file tamper detection. Current `charts[].hash` stores this value. |
| `scoreHash` | Ranking/score submission. Excludes visual effects and editor state. |
| `visualHash` | Visual-effect identity including BGA/media events. |

Chart hashes use compact canonical JSON:

```text
sha256-compact-canonical-json
```

Players and tools first load a chart into the internal chart model, then generate a compact-json canonical form with stable sorting and fixed default/null omission rules.
That compact canonical JSON is encoded as UTF-8 and hashed with SHA-256.
Readable-json and compact-json representations of the same chart therefore produce the same hash.

`nbms.editor` is excluded from all gameplay/package chart hashes. `nbms.longNote` semantic data is included in `chartHash` and `scoreHash`, while conversion-evidence-only fields such as `nbms.longNote.bmsCompat` are excluded. `nbms.visual` and `mediaEvents[]` are included in `chartHash` / `visualHash`, but excluded from `scoreHash` by default.

See also: [../spec/hash_canonicalization.md](../spec/hash_canonicalization.md)

## Security

The header contains security metadata:

- signed / unsigned
- encrypted / unencrypted
- public key metadata
- edit policy

Encryption and signing are still draft topics. The current public Editor/Viewer should support open projects first.
