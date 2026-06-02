
# NBMS Studio仕様 日本語版

NBMS Studioは、NBMSフォーマットを検証するための実験的なEditor/Viewerです。

## 目的

- Windows環境で動作し、環境差をなるべく小さくする
- C# / .NET 8 / Avaloniaで実装する
- EditorとViewerを1つのアプリケーションに統合する
- `song.nbmh`、`audio.nbma`、`score/*.nbmc` を読み込む
- BMSフォルダからNBMSへ変換する
- BMS固有機能をcoreへ固定せず、可能な限り拡張可能にする

## メイン画面

## Editor

Editor viewには以下を含みます。

- プロジェクトメタデータ
- 複数譜面プロジェクト用の譜面セレクタ
- 譜面一覧
- 音源一覧
- 縦型Timeline
- Direct Input
- ノート表編集
- プロジェクト検証による参照問題表示

選択中の譜面が、編集対象およびViewer再生対象になります。

## Viewer

Viewerは別Windowで開きます。

現在の動作:

- 選択中譜面を再生
- 一時停止と再開
- 先頭へ戻す
- プレイフィール寄りのレーン表示
- background audio lane表示
- HiSpeed表示倍率
- Viewerを閉じたとき再生停止

## BMS変換

フォルダ変換は、選択フォルダ直下のBMSファイルを読み込み、1つのNBMSプロジェクトを出力します。

```text
song.nbmh
audio.nbma
score/
  normal.nbmc
  hyper.nbmc
  another.nbmc
```

対応済み変換:

- 通常ノート
- background audio
- 基本的なロングノート
- `#LNOBJ`
- BPM変更
- STOP
- 小節長変更
- OGG Vorbisの保持

## 音声

NBMS Studioは、 `.nbma` から再生対象ファイルを一時ディレクトリへ展開して再生します。

再生方式:

- `.ogg`: `NAudio.Vorbis`
- FLAC / WAV / MP3など: NAudio経由のWindows Media Foundation

## 拡張方針

RANDOM、BGA、高度なSCROLL、将来のplay modeは、可能な範囲で拡張モジュールとして扱う方針です。

---

# NBMS Studio Specification

NBMS Studio is an experimental Editor/Viewer for validating the NBMS format.

## Goals

- Run on Windows with minimal environment differences.
- Use C# / .NET 8 / Avalonia.
- Keep Editor and Viewer in one application.
- Load `song.nbmh`, `audio.nbma`, and `score/*.nbmc`.
- Support BMS folder conversion into NBMS.
- Keep the format extensible instead of hard-coding all BMS behavior.

## Main Screens

## Editor

The Editor view contains:

- project metadata fields
- chart selector for projects with multiple charts
- chart list
- audio list
- vertical timeline view
- direct note input panel
- note table editing
- reference issue list through project validation

The selected chart is the active chart for both editing and Viewer playback.

## Viewer

The Viewer opens in a separate window.

Current behavior:

- play selected chart
- pause and resume
- return to beginning
- show playfield-style lanes
- show background audio lane
- support HiSpeed display multiplier
- stop playback when the Viewer window closes

## BMS Conversion

Folder conversion reads BMS files directly under the selected folder and outputs one NBMS project:

```text
song.nbmh
audio.nbma
score/
  normal.nbmc
  hyper.nbmc
  another.nbmc
```

Supported conversion targets include:

- notes
- background audio
- basic long notes
- `#LNOBJ`
- BPM changes
- STOP
- variable measure length
- OGG Vorbis preservation

## Audio

NBMS Studio currently extracts playable files from `.nbma` into a temporary cache.

Playback:

- `.ogg`: `NAudio.Vorbis`
- other supported files such as FLAC/WAV/MP3: Windows Media Foundation through NAudio

## Extension Direction

NBMS Studio should treat RANDOM, BGA, advanced scroll rules, and future play modes as extension/module topics where practical.

