# NBMS Editor State Audit 日本語版

作成日: 2026-06-20

`nbms.editor` は、譜面の再生結果に影響しないEditor固有状態を保存する任意拡張です。譜面hash、score hash、ranking hashの対象外にします。

## 現行UIで確認できる状態

| 項目 | 現行UI | `nbms.editor` 保存候補 | hash対象 |
| --- | --- | --- | --- |
| grid division | `EditorGridDivision` | `shared.grid.division` | No |
| snap on/off | `IsEditorSnapEnabled` | `shared.grid.snapEnabled` | No |
| view start tick | `EditorTimelineStartTick` | `local.viewStartTick` | No |
| selected panel | Chart/Events/Asset(audio)/Asset(media)相当 | `local.selectedPanel` | No |
| asset tab | Audio / Media | `local.assetTab` | No |
| collapsed lane group | 未実装 | `local.collapsedLaneGroups` | No |
| background audio lane count | BMSE準拠の複数BGM lane | `shared.backgroundAudioLaneCount` | No |

## 現状の実装判断

- 仕様本文には `nbms.editor` v0.1 を記載済み。
- 現在のStudioはUI状態をViewModel内で扱っているが、chart metadataへの永続化はまだ限定的。
- 初期公開では、`shared.grid` と `shared.backgroundAudioLaneCount` を先に保存対象にするのが安全。
- `local.*` はユーザー環境依存なので、設定で明示的に有効にした場合だけ保存する。
- ViewerとMonoGame Viewerは `nbms.editor` を無視する。

## 実装チェックリスト

- [ ] `NbmsEditorState` modelを追加する。
- [ ] chart metadataの `metadata.extensions["nbms.editor"]` を読み取るhelperを追加する。
- [ ] 未知フィールドを破壊しないroundtrip方針を決める。
- [ ] open時に `shared.grid.division` / `shared.grid.snapEnabled` をUIへ反映する。
- [ ] save時に `shared.grid.division` / `shared.grid.snapEnabled` をmetadataへ保存する。
- [ ] local保存設定を追加する。
- [ ] local保存有効時だけ `local.viewStartTick` / `local.selectedPanel` / `local.assetTab` を保存する。
- [ ] `nbms.editor` をcanonical chart hashから除外する処理を確認する。

---

# NBMS Editor State Audit

`nbms.editor` is an optional extension for editor-only state. It does not affect playback and must be excluded from chart, score, and ranking hashes.

The current public-safe implementation path is to persist shared grid settings first, then add local state only behind an explicit user setting.
