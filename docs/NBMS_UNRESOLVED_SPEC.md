
# NBMS仕様の未決定事項 日本語版

このドキュメントは、まだ正式に決まっていない項目を整理するためのものです。

## フォーマット安定性

- 最終的なバージョン番号と互換性ルール
- draft 0.1ファイルを将来の安定版Playerで読めるようにするか
- 未知のフィールドをどの程度厳密に扱うか

## 拡張モジュール

NBMSでは、coreに固定すべきでない機能を拡張モジュールとして扱う方針です。

未決定事項:

- RANDOM / IF / SWITCH系の分岐
- BGAとメディアタイミング
- 特殊レーンや別プレイモード
- MVPを超えるSCROLL / SPEED変更
- 拡張依存関係の解決
- 未対応の必須拡張がある場合に読み込みを止めるか、部分表示を許可するか

## BMS互換性

プロトタイプ変換ではcore timingと基本的なLNには対応していますが、完全なBMS互換は未完了です。

未決定事項:

- RANDOM展開方針
- LNOBJの方言差
- invisible note / mine note
- BGAチャンネルとBMP/AVI変換
- 同tickのBPM/STOPイベントの扱い
- 古いBMSアーカイブに含まれるファイル名エンコーディング

## 音源ポリシー

現在のプロトタイプでは、既存の音源ファイルを必須変換せず `.nbma` に格納します。

未決定事項:

- 安定版NBMSでFLAC必須にするか、OGG Vorbisも正式対応にするか
- 長い音声のストリーミング規則
- 音量正規化
- loop metadata
- entry単位の暗号化
- codec対応を必須仕様にするか、機能交渉にするか

## 権利と作者性

NBMSにはrights metadataがありますが、運用ポリシーは未確定です。

未決定事項:

- 音源制作者のクレジット方法
- 署名済み譜面の編集をEditorが禁止するべきか
- 差分譜面・二次創作譜面の表現
- 暗号化されていないopen projectでコミュニティ譜面をどう扱うか

## ハッシュとランキング

現在の譜面ハッシュはcanonical JSONを使います。

未決定事項:

- 言語間での浮動小数点canonicalization
- どのmetadataをランキング同一性に含めるか
- timing修正だけで別ランキングhashにするか
- RANDOMや拡張モジュールを使う譜面のhash規則

## パッケージング

未決定事項:

- `.nbmp` のインストール動作
- パッケージ署名
- パッケージ単位の依存関係
- メディアパッケージ `.nbmg` の最終構造

## Editor動作

未決定事項:

- 保存時に元のJSON整形を保持するか
- autosave / backup rules
- Undo / Redo
- 未対応拡張を含む譜面の編集モード

---

# NBMS Unresolved Specification Topics

This document lists topics that are intentionally not final.

## Format Stability

- Final version number and compatibility rules.
- Whether draft 0.1 files should be loadable by future stable players.
- How strict readers should be with unknown fields.

## Extension Modules

NBMS should support extension modules for features that should not hard-code into the core format.

Open topics:

- RANDOM / IF / SWITCH style branching.
- BGA and media timing.
- special lanes and alternate play modes.
- scroll and speed changes beyond MVP.
- extension dependency resolution.
- whether unsupported required extensions should block loading or allow partial viewing.

## BMS Compatibility

The prototype converter supports core timing and basic long notes, but full BMS compatibility remains open.

Open topics:

- RANDOM expansion policy.
- LNOBJ variants and BMS-player-specific behavior.
- invisible notes and mine notes.
- BGA channels and BMP/AVI conversion.
- BPM/STOP edge cases with simultaneous events.
- filename encoding differences in old BMS archives.

## Audio Policy

Current prototype behavior stores existing audio files in `.nbma` without mandatory transcoding.

Open topics:

- whether stable NBMS should require FLAC only or allow OGG Vorbis as a first-class codec.
- streaming rules for long audio.
- volume normalization.
- loop metadata.
- per-entry encryption.
- whether audio decode support should be mandatory or feature-negotiated.

## Rights and Authorship

NBMS has rights metadata, but policy details are not final.

Open topics:

- how sound-source authors are credited.
- whether editors should prevent modification of signed charts.
- how derivative charts and score additions are represented.
- how unencrypted open projects allow community-made charts.

## Hash and Ranking Rules

Current chart hash rules use canonical JSON.

Open topics:

- exact floating-point canonicalization across languages.
- which metadata should affect ranking identity.
- whether timing-only corrections should create new ranking hashes.
- how to hash charts that use RANDOM or extension modules.

## Packaging

Open topics:

- `.nbmp` installer behavior.
- package signing.
- package-level dependency references.
- media package `.nbmg` final container structure.

## Editor Behavior

Open topics:

- whether editor saves should preserve original formatting.
- autosave and backup file rules.
- undo/redo expectations.
- how to handle unsupported extensions in editing mode.
