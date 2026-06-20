# NBMS to BMS Export / Loss Report 日本語版

作成日: 2026-06-20

NBMS StudioのBMS exportは、完全な逆変換ではなく、既存BMSツールで互換確認するための制限付き出力として扱う。

## 出力対象

- tap note
- hold note
- background audio
- BPM変更
- STOP
- 小節長
- 5keys / 7keys / 10keys / 14keysの標準的なplay lane

## loss report対象

以下はBMS標準へ完全に戻せないため、export時にloss reportへ出す。

- `mediaEvents`
- `.nbmg` 内の画像/動画container構造
- `nbms.scroll`
- `nbms.random` のpreserved branch
- 未対応extension
- encrypted audio/media container
- CN/HCNなど、BMS標準のLNとして意味が落ちるlong note mode
- audioId/mediaIdがBMSの2桁/36進IDへ安全に戻せない場合
- volume / panなどBMS標準で表現できないnote property

## 出力方針

- audioIdはexport時に `01` から順番にBMS定義IDへ割り当てる。
- NBMS Studioから出力する場合、`.nbma` 内の参照音声assetは `.bms` と同じフォルダへ展開する。
- mediaIdも同様に `01` から順番に割り当てる。
- `bga` / `video` / `layer` / `poor` media eventは、BMSの `#xxx04` / `#xxx07` / `#xxx06` へ最小変換する。
- NBMS Studioから出力する場合、`.nbmg` 内の参照media assetは `.bms` と同じフォルダへ展開する。
- 出力先には `.bms` と `export-loss-report.md` を並べる。
- 元のNBMSは変更しない。
- lossが1件以上あってもexport自体は可能にする。ただしUI上ではwarningとして表示する。
- encrypted containerは素材を書き出さず、chart textのみexportする。素材不足はloss reportへ記録する。

## 実装ロードマップ

- [x] `BmsExportService` をCoreまたはApp Import/Export層へ追加する。
- [x] `BmsExportLossReport` modelを追加する。
- [x] 標準laneからBMS channelへ変換する最小mapperを追加する。
- [x] `#WAVxx` 定義IDの再割り当てを追加する。
- [ ] `#BMPxx` 定義IDの再割り当てを追加する。
- [x] tick列をBMS measure dataへ再量子化する。
- [x] BPM/STOP/小節長の最小出力を追加する。
- [x] loss reportをMarkdownで出力する。
- [x] Studio UIに「BMS互換出力...」を追加する。
- [x] 回帰テストにtap/BGM/BPM/STOP/小節長のexport fixtureを追加する。

2026-06-20時点の最小実装では、音源ファイルは `.nbma` から、画像/動画ファイルは `.nbmg` から `.bms` と同じフォルダへ展開します。BGA/LAYER/POORはBMS channelへ変換しますが、NBMS固有のlayer合成、clear、opacity、blendなどはloss report対象です。

---

# NBMS to BMS Export / Loss Report

NBMS Studio BMS export is a limited compatibility export, not a complete reverse conversion.

## Exported Scope

- tap notes
- hold notes
- background audio
- BPM changes
- STOP events
- measure length
- standard play lanes for 5keys / 7keys / 10keys / 14keys

## Loss Report Scope

The following items are reported because they cannot be represented safely in standard BMS:

- `mediaEvents`
- `.nbmg` image/video container structure
- `nbms.scroll`
- preserved `nbms.random` branches
- unsupported extensions
- encrypted audio/media containers
- CN/HCN or other long note modes that lose meaning when reduced to standard LN
- audioId/mediaId values that cannot be mapped safely to BMS IDs
- note properties such as volume and pan

## Export Policy

- audioId values are reassigned to BMS IDs from `01`.
- When exporting from NBMS Studio, referenced audio assets in `.nbma` are extracted next to the `.bms` file.
- mediaId values are reassigned to BMS IDs from `01`.
- `bga` / `video` / `layer` / `poor` media events are converted to BMS `#xxx04` / `#xxx07` / `#xxx06` channels.
- When exporting from NBMS Studio, referenced media assets in `.nbmg` are extracted next to the `.bms` file.
- The exporter writes both `.bms` and `export-loss-report.md`.
- The source NBMS project is never modified.
- Export may continue even when losses exist, but the UI must show warnings.
- Encrypted containers do not export assets; missing assets are written to the loss report.
