# NBMS 0.1 Phase 1 Specification

This directory contains the Phase 1 specification artifacts for the NBMS draft.

Files:

- `header.schema.json`: JSON Schema for `.nbmh` header files.
- `chart.schema.json`: JSON Schema for `.nbmc` chart files.
- `audio_container.md`: MVP container specification for `.nbma` audio archives.
- `hash_canonicalization.md`: Environment-independent chart hash rules.
- `package_container.md`: MVP distribution package specification for `.nbmp`.

Phase 1 intentionally avoids encryption and signing implementation details. Those are specified as metadata fields and will be implemented in a later phase after basic loading, validation, and playback are proven.

---

# NBMS 0.1 Phase 1 仕様 日本語版

このディレクトリには、NBMSドラフトのPhase 1仕様資料を置いています。

ファイル:

- `header.schema.json`: `.nbmh` ヘッダーファイル用JSON Schema
- `chart.schema.json`: `.nbmc` 譜面ファイル用JSON Schema
- `audio_container.md`: `.nbma` 音源アーカイブのMVP仕様
- `hash_canonicalization.md`: 環境依存しない譜面hash規則
- `package_container.md`: `.nbmp` 配布パッケージMVP仕様

Phase 1では、暗号化と署名の詳細実装は意図的に避けています。これらはmetadata fieldとして定義し、基本的な読み込み、検証、再生が確認できた後のPhaseで実装する想定です。
