
# NBMS Canonical JSON Hashing 日本語版

## 1. 目的

譜面hashは、OS、Editor、JSON整形の違いに左右されず安定している必要があります。同じ論理的な `.nbmc` は、indent、line ending、object key orderが違っても同じhashになるべきです。

## 2. Algorithm Identifier

ヘッダーでは以下を使います。

```text
sha256-compact-canonical-json
```

## 3. Canonicalization Rules

`NBMS 0.2 compact-json` では、譜面hash計算に以下の規則を使います。

1. `.nbmc` をJSONとしてparseする
2. `encoding == "compact-json"` ならcompact chartとして読む
3. `encoding` がない従来readable-jsonなら内部chart modelへ読む
4. 内部chart modelからcompact chartを生成する
5. timing、notes、background audio、media eventsのsort順を固定する
6. default値とnull値を仕様どおり省略する
7. object keyをUnicode code point orderで再帰的にsortする
8. array orderはcompact生成後の順序を保持する
9. 不要なwhitespaceなしでJSONを出力する
10. canonical compact JSON textをUTF-8でencodeする
11. UTF-8 bytesに対してSHA-256を計算する
12. `sha256-` prefixつきlowercase hexadecimalとして保存する

## 4. Hash種別

NBMSでは用途が異なるhashを分けます。

| hash | algorithm | 用途 |
| --- | --- | --- |
| `chartHash` | `sha256-compact-canonical-json` | package整合性、譜面ファイル改変検出。 |
| `scoreHash` | `sha256-nbms-score-canonical-json` | ランキング/スコア送信用。視覚演出やEditor状態の差を除外する。 |
| `visualHash` | `sha256-nbms-visual-canonical-json` | BGA/media eventを含めた視覚演出の同一性確認。 |
| `audioManifestHash` | `sha256-file` またはmanifest canonical hash | `.nbma` の整合性確認。 |
| `mediaManifestHash` | `sha256-file` またはmanifest canonical hash | `.nbmg` の整合性確認。 |

現行headerの `charts[].hash` は `chartHash` として扱います。

## 5. chartHash対象

`chartHash` の対象は譜面の論理object全体です。ただしEditor/ローカル状態は除外します。

hashに影響するもの:

- Timing events
- Notes
- Lane definitions
- Background audio events
- Media events
- Chart metadata。ただし `metadata.extensions["nbms.editor"]` と、互換情報だけの `bmsCompat` は除外

hashに影響しないもの:

- file indentation
- CRLF / LF line endings
- object key order
- readable-json / compact-json / pretty compact-json の整形差
- `nbms.editor` のgrid、表示開始tick、選択tabなど

## 6. scoreHash対象

`scoreHash` はランキング/スコア送信用で、プレイ結果に影響する情報だけを対象にします。

含めるもの:

- Timing events: `bpm`, `stop`, `bar`, measure length
- Playable notes: lane, tick, type, duration, longNote mode, mineなど
- 判定へ影響する拡張: `nbms.longNote`, 展開済み `nbms.random`, 将来の判定系extension
- playable noteの `audioId`。無音差分を別譜面として扱うため

除外するもの:

- `nbms.editor`
- `mediaEvents[]` と `nbms.visual`
- `.nbmg` manifest
- stagefile/banner/preview
- `nbms.bmsCompat` や `nbms.longNote.bmsCompat` のような変換根拠のみのmetadata

`nbms.random` は未展開状態を直接hashしません。Importer/Playerが同じseedまたは選択結果から展開した後のchartを `scoreHash` 対象にします。未展開RANDOMを含むchartはランキング非対応、またはrandom branch identifierを別途付けます。

## 7. visualHash対象

`visualHash` は視覚演出の同一性確認用です。`mediaEvents[]`, `nbms.visual`, `.nbmg` manifestのmediaId/path/hash/kind/codecを対象にします。スコアランキングには原則使いません。

## 8. 実装上の未決定メモ

浮動小数点正規化は、`NBMS 1.0` 確定前に複数言語で検証する必要があります。それまでは、譜面file内で不要な小数精度を避けるべきです。

---


# NBMS Canonical JSON Hashing

## 1. Purpose

Chart hashes must be stable across operating systems, editors, and JSON formatting choices. The same logical `.nbmc` chart must produce the same hash regardless of indentation, line endings, or object key order.

## 2. Algorithm Identifier

Headers use:

```text
sha256-compact-canonical-json
```

## 3. Canonicalization Rules

For `NBMS 0.2 compact-json`, chart hash calculation uses the following rules:

1. Parse the `.nbmc` file as JSON.
2. If `encoding == "compact-json"`, read it as a compact chart.
3. If `encoding` is absent, read it as the legacy readable-json chart form.
4. Convert the internal chart model to a compact chart.
5. Use stable sort order for timing, notes, background audio, and media events.
6. Omit default and null values according to the compact chart rules.
7. Recursively sort object keys by Unicode code point order.
8. Preserve array order after compact generation.
9. Emit JSON without insignificant whitespace.
10. Encode the canonical compact JSON text as UTF-8.
11. Calculate SHA-256 over those UTF-8 bytes.
12. Store the result as lowercase hexadecimal with the `sha256-` prefix.

## 4. Hash Types

NBMS separates hashes by purpose.

| hash | algorithm | purpose |
| --- | --- | --- |
| `chartHash` | `sha256-compact-canonical-json` | Package consistency and chart-file tamper detection. |
| `scoreHash` | `sha256-nbms-score-canonical-json` | Ranking/score submission. Excludes visual effects and editor state. |
| `visualHash` | `sha256-nbms-visual-canonical-json` | Visual-effect identity including BGA/media events. |
| `audioManifestHash` | `sha256-file` or manifest canonical hash | `.nbma` consistency. |
| `mediaManifestHash` | `sha256-file` or manifest canonical hash | `.nbmg` consistency. |

The current header `charts[].hash` is treated as `chartHash`.

## 5. chartHash Target

The `chartHash` target is the whole logical chart object, excluding editor/local state.

The following affect the hash:

- Timing events
- Notes
- Lane definitions
- Background audio events
- Media events
- Chart metadata, except `metadata.extensions["nbms.editor"]` and compatibility-only `bmsCompat` data

The following do not affect the hash:

- File indentation
- CRLF versus LF line endings
- Object key order
- readable-json / compact-json / pretty compact-json formatting
- `nbms.editor` grid, view start tick, selected tab, and similar editor state

## 6. scoreHash Target

`scoreHash` is for ranking/score submission and includes only gameplay-affecting data.

Included:

- Timing events: `bpm`, `stop`, `bar`, and measure length
- Playable notes: lane, tick, type, duration, long-note mode, mines, and similar objects
- Judgement-affecting extensions: `nbms.longNote`, expanded `nbms.random`, and future judgement extensions
- Playable-note `audioId`, so silent/changed-sound charts can be separated

Excluded:

- `nbms.editor`
- `mediaEvents[]` and `nbms.visual`
- `.nbmg` manifest
- stagefile/banner/preview
- Conversion-evidence-only metadata such as `nbms.bmsCompat` and `nbms.longNote.bmsCompat`

`nbms.random` is not hashed directly in unresolved form. Importers/players hash the expanded chart produced by the same seed or branch selection. Charts with unresolved RANDOM are either ranking-ineligible or require a separate random branch identifier.

## 7. visualHash Target

`visualHash` checks visual-effect identity. It includes `mediaEvents[]`, `nbms.visual`, and `.nbmg` manifest mediaId/path/hash/kind/codec. It is not used for score rankings by default.

## 8. Example Output

```text
sha256-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
```

## 9. Open Implementation Note

The exact treatment of floating-point normalization should be tested across target languages before finalizing `NBMS 1.0`. Until then, authors should avoid unnecessary fractional precision in chart files.
