# NBMS Public Review Guide 日本語版

作成日: 2026-06-20

GitHubで意見を募る時の入口です。NBMSはまだ正式仕様ではなく、NBMS StudioもEditor/Viewer検証用の実験実装です。

## まず見てほしい文書

- `docs/NBMS_USAGE.md`
  - NBMSプロジェクトの最小構成と扱い方。
- `docs/NBMS_SPEC.md`
  - 現在のフォーマット仕様ドラフト。
- `docs/NBMS_UNRESOLVED_SPEC.md`
  - 未決定の仕様項目。
- `NBMS.Studio/docs/NBMS_STUDIO_SPEC.md`
  - NBMS Studioの方針。
- `NBMS.Studio/docs/BUILD.md`
  - Windows / .NET 8でのBuild手順。
- `NBMS.Studio/docs/UNIMPLEMENTED.md`
  - 未実装・未検討事項。

## 特に意見が欲しいところ

- BMS import互換性。
  - RANDOM/IF preserve-onlyで初期公開してよいか。
  - SCROLL/SPEEDなど拡張命令の扱い。
  - LNOBJ/LNTYPE/LNMODEの保存位置。
- 音源/メディアコンテナ。
  - OGGをそのまま扱うか、FLAC変換を推奨するか。
  - `.nbma` / `.nbmg` の改変検出、署名、暗号化の優先度。
- Editor体験。
  - BMSE相当で最低限必要な編集機能。
  - `nbms.editor` に保存してよい共有設定とlocal設定の境界。
- Viewer体験。
  - MonoGame ViewerのBGA/音声同期。
  - ffmpegを外部必須にするか、任意fallbackに留めるか。

## サンプル募集

- `#SPEEDxx` / `#xxxSP` を使うBMS。
- PMS/OCT/FP/iBMSCなど、7keys/14keys以外の譜面。
- MPEG/AVI/MP4/WMVなど複数codecのBGA。
- 音数1000以上の高密度譜面。
- RANDOM/IFを多用する譜面。

## 現時点の非目標

- LR2後継Player本体。
- IR連携。
- LR2 skin runtime完全互換。
- 強DRM。
- 完全なBMS逆変換。

---

# NBMS Public Review Guide

This is the entry point for GitHub review. NBMS is not a finalized specification yet, and NBMS Studio is an experimental Editor/Viewer implementation.

## Recommended Reading

- `docs/NBMS_USAGE.md`: minimal NBMS project structure and usage.
- `docs/NBMS_SPEC.md`: current format specification draft.
- `docs/NBMS_UNRESOLVED_SPEC.md`: unresolved specification topics.
- `NBMS.Studio/docs/NBMS_STUDIO_SPEC.md`: NBMS Studio direction.
- `NBMS.Studio/docs/BUILD.md`: Windows / .NET 8 build instructions.
- `NBMS.Studio/docs/UNIMPLEMENTED.md`: unimplemented or unresolved topics.

## Feedback Wanted

- BMS import compatibility.
- Audio/media container policy.
- Editor workflow and `nbms.editor` state.
- MonoGame Viewer playback, BGA, and ffmpeg policy.

## Samples Wanted

- BMS files using `#SPEEDxx` / `#xxxSP`.
- PMS/OCT/FP/iBMSC charts.
- BGA videos with multiple codecs.
- Dense charts with 1000+ audio definitions.
- Charts using RANDOM/IF heavily.
