
# NBMS Canonical JSON Hashing 日本語版

## 1. 目的

譜面hashは、OS、Editor、JSON整形の違いに左右されず安定している必要があります。同じ論理的な `.nbmc` は、indent、line ending、object key orderが違っても同じhashになるべきです。

## 2. Algorithm Identifier

ヘッダーでは以下を使います。

```text
sha256-canonical-json
```

## 3. Canonicalization Rules

`NBMS 0.1` では、譜面hash計算に以下の規則を使います。

1. `.nbmc` をJSONとしてparseする
2. duplicate object keysを拒否する
3. object keyをUnicode code point orderで再帰的にsortする
4. array orderはそのまま保持する
5. 不要なwhitespaceなしでJSONを出力する
6. stringはJSON escaping rulesに従って出力する
7. numberは値を保持する最短JSON表現で出力する
8. canonical JSON textをUTF-8でencodeする
9. UTF-8 bytesに対してSHA-256を計算する
10. `sha256-` prefixつきlowercase hexadecimalとして保存する

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
sha256-canonical-json
```

## 3. Canonicalization Rules

For `NBMS 0.1`, chart hash calculation uses the following rules:

1. Parse the `.nbmc` file as JSON.
2. Reject duplicate object keys.
3. Recursively sort object keys by Unicode code point order.
4. Preserve array order exactly.
5. Emit JSON without insignificant whitespace.
6. Emit strings using JSON escaping rules.
7. Emit numbers in the shortest JSON representation that preserves the parsed numeric value.
8. Encode the canonical JSON text as UTF-8.
9. Calculate SHA-256 over those UTF-8 bytes.
10. Store the result as lowercase hexadecimal with the `sha256-` prefix.

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