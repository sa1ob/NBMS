# NBMS Draft / NBMS Studio 日本語版

NBMSは、BMS系リズムゲーム譜面を扱うための次世代フォーマット案です。  
※名称も募集中

このリポジトリには現在、以下を含めています。

- NBMSフォーマット関連ドキュメント
- まだ未決定の仕様メモ
- 実験的なEditor/Viewer実装であるNBMS Studio

このプロジェクトは、まだ正式仕様ではありません。  
たたき台として活用されることを前提として公開しているものです。  
また、ちゃんと作ってくれる人募集中

## リポジトリの内容について
改変・改造・再配布自由

## 目的

BMSフォーマットの最小構成は以下のようなイメージ
```text
7k.bme
001.WAV
002.WAV
003.WAV
004.WAV
.......
999.WAV
```
これを以下の形にできないか？というフォーマット案です。
```text
song.nbmh　・・・ヘッダファイル
audio.nbma　・・・オーディオコンテナ
score/
  main.nbmc　・・・譜面ファイル
```
BGAも同様の形式に映像コンテナ/メディアコンテナ `.nbmg` として扱う実験実装を進めています。
動画BGAの再生には、現時点では `ffmpeg` をインストールしてPATHから実行できる状態にしておく必要があります。


### メリット
- zipで配布されているBMSの書庫を解凍したときにダバァしなくて済む
- PC内での移動やバックアップが容易になる
- 音声コンテナや映像コンテナに鍵をかけて編集不可にすることで保護しながら配布できる（必須ではない）
- 拡張することで音数の個数制限を実質的に撤廃できる

### デメリット
- コンテナを展開しながら再生する都合上、再生時のオーバーヘッドが発生する
- 移行の際のエネルギーが、作者側もプレイヤー側も重め
  - BMSプレイヤー(LR2/beatoraja等のこと)側の実装も提供しなければならない

### 未考慮点
- あくまでフォーマット案なのでBMSプレイヤー側で実装する方面のメリットデメリットは別検討が必要で考慮していない。

## 現在の状態
- EditorアプリとViewerアプリのみ提供
- 既存BMSから新フォーマットへの変換と再生のみ可能
- 譜面ファイル `.nbmc` はJSONのままcompact-json layoutで保存する方針に移行中

### 使い方
- [NBMS Studio Build手順](NBMS.Studio/docs/BUILD.md)をみてBuild必要  
※配布するほどの完成度はないためBuildできる人向けです。


## ドキュメント

- [NBMS利用ガイド](docs/NBMS_USAGE.md)
- [公開レビューガイド](docs/NBMS_PUBLIC_REVIEW_GUIDE.md)
- [NBMSフォーマット仕様ドラフト](docs/NBMS_SPEC.md)
- [NBMS仕様の未決定事項](docs/NBMS_UNRESOLVED_SPEC.md)
- [`nbms.editor` 保存情報棚卸し](docs/NBMS_EDITOR_STATE_AUDIT.md)
- [BMS互換出力とloss report方針](docs/NBMS_BMS_EXPORT_LOSS_REPORT.md)

## NBMS Studio

- [NBMS Studio概要](NBMS.Studio/docs/NBMS_STUDIO_SPEC.md)
- [NBMS Studio Build手順](NBMS.Studio/docs/BUILD.md)
- [NBMS Studioの未実装・未検討事項](NBMS.Studio/docs/UNIMPLEMENTED.md)

## 現在の状態

NBMSおよびNBMS Studioは未完成です。
現在の実装は、フォーマット案、BMS変換、音源アーカイブ、基本的なEditor/Viewerワークフローを検証するためのものです。

---
# NBMS Draft / NBMS Studio

NBMS is a proposed next-generation format for handling BMS-style rhythm game charts.  
The name itself is also still open for discussion.

This repository currently contains:

- NBMS format-related documents
- Notes on specifications that have not yet been finalized
- NBMS Studio, an experimental Editor/Viewer implementation

This project is not yet an official specification.  
It is being published as a starting point for discussion and further refinement.
We're also looking for someone who can make them properly

## Repository Contents

Modification, customization, and redistribution are allowed.

## Purpose

A minimal BMS package often looks like this:

```text
7k.bme
001.WAV
002.WAV
003.WAV
004.WAV
.......
999.WAV
```

NBMS is a format proposal that explores whether this can be represented more like this:

```text
song.nbmh ... header file
audio.nbma ... audio container
score/
  main.nbmc ... chart file
```

BGA is now being prototyped as a separate video/media container, `.nbmg`.
Video BGA playback currently requires `ffmpeg` to be installed and available from PATH.

### Benefits

- Avoids scattering many files when extracting a zipped BMS archive.
- Makes moving and backing up files on a PC easier.
- Allows optional protection by locking audio or media containers so they cannot be edited directly.
- Can effectively remove the existing sound-count limit through extension.

### Drawbacks

- Playback may have overhead because the player needs to read from containers while playing.
- Migration requires effort from both creators and players.
  - BMS players such as LR2 or beatoraja would also need implementation support.

### Not Yet Considered

- This is only a format proposal. The pros and cons of implementing it in existing BMS players need separate discussion.

## Current State

- Only Editor and Viewer applications are provided.
- Existing BMS files can be converted to the new format and played back.
- Chart files `.nbmc` are being moved to a compact-json layout while remaining plain JSON.

### Usage

- See the [NBMS Studio Build Guide](NBMS.Studio/docs/BUILD.md) and build the application yourself.  
This is intended for people who can build it, because it is not complete enough for normal distribution yet.

## Documents

- [NBMS Usage Guide](docs/NBMS_USAGE.md)
- [Public Review Guide](docs/NBMS_PUBLIC_REVIEW_GUIDE.md)
- [NBMS Format Specification Draft](docs/NBMS_SPEC.md)
- [Unresolved NBMS Specification Topics](docs/NBMS_UNRESOLVED_SPEC.md)
- [`nbms.editor` State Audit](docs/NBMS_EDITOR_STATE_AUDIT.md)
- [BMS Compatibility Export and Loss Report Policy](docs/NBMS_BMS_EXPORT_LOSS_REPORT.md)

## NBMS Studio

- [NBMS Studio Overview](NBMS.Studio/docs/NBMS_STUDIO_SPEC.md)
- [NBMS Studio Build Guide](NBMS.Studio/docs/BUILD.md)
- [NBMS Studio Unimplemented / Unresolved Topics](NBMS.Studio/docs/UNIMPLEMENTED.md)

## Current Status

NBMS and NBMS Studio are incomplete.  
The current implementation is intended to validate the format proposal, BMS conversion, audio archive handling, and basic Editor/Viewer workflows.
