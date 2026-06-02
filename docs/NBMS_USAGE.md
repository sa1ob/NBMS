
# NBMS利用ガイド 日本語版

NBMSは現在ドラフト段階のフォーマットです。現時点の最小プロジェクト構成は以下です。

```text
song.nbmh
audio.nbma
score/
  main.nbmc
  another.nbmc
```

## プロジェクトを開く

プロジェクトの入口として `song.nbmh` を開きます。ヘッダーは以下を参照します。

- 音源アーカイブ: `audio.nbma`
- 1つ以上の譜面ファイル: `score/` 配下
- 将来的なメディアコンテナ: `media.nbmg` など

## 譜面を編集する

`song.nbmh` に複数譜面が定義されている場合、Editorは編集・再生対象の譜面を選択できる必要があります。

NBMS Studioでは、Editor左ペインの譜面セレクタで対象譜面を選択できます。

## 譜面を再生する

Playerは選択された `.nbmc` を読み込み、noteおよびbackground audioの `audioId` を `audio.nbma/manifest.json` から解決し、Timingに従って再生します。

現在対応しているTiming:

- 初期BPM
- BPM変更
- STOP
- 小節線
- BMS `#xxx02` 由来の小節長変更

## BMSから変換する

プロトタイプ変換機能では、BMSフォルダを1つのNBMSプロジェクトに変換します。

```text
output/
  song.nbmh
  audio.nbma
  score/
    normal.nbmc
    hyper.nbmc
    another.nbmc
```

同じフォルダ内の複数BMSファイルは、1つの `song.nbmh` にまとめて登録されます。

音源ファイルは必須の再エンコードを行わず `audio.nbma` に格納します。現在のプロトタイプ再生では、FLAC / WAV / MP3 はMedia Foundation経由、OGG Vorbisは `NAudio.Vorbis` 経由で再生します。

---

# NBMS Usage Guide

NBMS is currently a draft format. The current minimum project layout is:

```text
song.nbmh
audio.nbma
score/
  main.nbmc
  another.nbmc
```

## Opening a Project

Open `song.nbmh` as the project header. The header references:

- one audio archive: `audio.nbma`
- one or more chart files under `score/`
- optional future media containers such as `media.nbmg`

## Editing a Chart

When multiple charts are defined in `song.nbmh`, an editor should let the user select which chart is being edited or previewed.

NBMS Studio currently exposes this as a chart selector in the Editor left pane.

## Playing a Chart

The player reads the selected `.nbmc` chart, resolves note and background audio `audioId` references through `audio.nbma/manifest.json`, then plays events according to the timing map.

Timing currently supports:

- initial BPM
- BPM changes
- STOP events
- measure/bar lines
- variable measure length converted from BMS `#xxx02`

## Converting from BMS

The prototype converter can convert a BMS folder into one NBMS project:

```text
output/
  song.nbmh
  audio.nbma
  score/
    normal.nbmc
    hyper.nbmc
    another.nbmc
```

Multiple BMS files in the same folder are registered together in `song.nbmh`.

Audio files are stored in `audio.nbma` without mandatory transcoding. Current prototype playback supports FLAC, WAV, MP3 through Media Foundation, and OGG Vorbis through `NAudio.Vorbis`.
