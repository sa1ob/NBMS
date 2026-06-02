# NBMS Studio 未実装・未検討事項 日本語版

NBMS Studioはプロトタイプです。以下は未実装または未確定です。

## Editor

- Timeline上での本格的なマウス編集
- grid snapping controls
- copy / paste
- undo / redo
- autosave / backup
- 譜面の新規作成・削除
- lane設定UI
- advanced note properties
- extension-aware editing
- BGA / media editing

## Viewer

- 判定とスコア
- 入力処理
- gauge
- skinning
- 高精度・低遅延の音声スケジューリング
- seek bar
- MVPを超えるspeed change visualization
- 複数play mode向けの表示オプション

## BMS変換

- RANDOM / IF / SWITCH
- BGAとBMP/AVI処理
- mine note
- invisible note
- LN方言の完全互換
- 古いアーカイブのファイル名エンコーディング検出
- 音源metadata抽出
- FLACへの任意transcode

## Packaging and Security

- 署名付きproject保存
- 暗号化project保存
- 作者鍵管理UI
- package signing
- `.nbmp` のinstaller/import workflow

## Validation

- schema validation UI
- 詳細な参照診断
- unsupported extension warnings
- codec support warnings
- BMS互換性検証用の自動test corpus

## 公開時に意見が欲しい項目

- OGG Vorbisをそのまま対応し続けるか、FLAC変換を推奨するか
- 差分譜面・二次創作譜面の表現方法
- edit lockやsignatureをどの程度厳密にするか
- BMS互換性のうち、どの機能を優先的に対応すべきか

---

# NBMS Studio Unimplemented / Unresolved Topics

NBMS Studio is a prototype. The following areas are not final.

## Editor

- full mouse editing on the timeline
- grid snapping controls
- copy / paste
- undo / redo
- autosave and backups
- chart creation and deletion
- lane configuration UI
- advanced note properties
- extension-aware editing
- BGA/media editing

## Viewer

- judgment and score system
- input handling
- gauge
- skinning
- precise low-latency audio scheduling
- seek bar
- speed change visualization beyond MVP
- display options for multiple play modes

## BMS Conversion

- RANDOM / IF / SWITCH
- BGA and BMP/AVI handling
- mine notes
- invisible notes
- complete LN dialect compatibility
- filename encoding detection for old archives
- full audio metadata extraction
- optional transcoding to FLAC

## Packaging and Security

- signed project save
- encrypted project save
- author key management UI
- package signing
- installer/import workflow for `.nbmp`

## Validation

- schema validation UI
- detailed reference diagnostics
- unsupported extension warnings
- codec support warnings
- automated test corpus for BMS compatibility

## Public Feedback Topics

Feedback is especially useful for:

- whether OGG Vorbis should remain supported as-is or be converted to FLAC
- how derivative charts should be represented
- how strict edit locks and signatures should be
- which BMS compatibility features should be prioritized first
