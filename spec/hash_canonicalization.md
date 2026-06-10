
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

## 4. Hash対象

hash対象は譜面object全体です。

hashに影響するもの:

- Timing events
- Notes
- Lane definitions
- Background audio events
- Media events
- Chart metadata

hashに影響しないもの:

- file indentation
- CRLF / LF line endings
- object key order

## 5. 実装上の未決定メモ

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

## 4. Hash Target

The hash target is the whole logical chart object.

The following affect the hash:

- Timing events
- Notes
- Lane definitions
- Background audio events
- Media events
- Chart metadata

The following do not affect the hash:

- File indentation
- CRLF versus LF line endings
- Object key order

## 5. Example Output

```text
sha256-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
```

## 6. Open Implementation Note

The exact treatment of floating-point normalization should be tested across target languages before finalizing `NBMS 1.0`. Until then, authors should avoid unnecessary fractional precision in chart files.
