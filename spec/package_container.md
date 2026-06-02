
# NBMS Package `.nbmp` MVP 日本語版

## 1. 目的

`.nbmp` はNBMS 0.1の配布パッケージ形式です。

PlayerやEditorが扱う論理的な3ファイル構成を保ったまま、最小限の再生可能project fileを1つのZIP archiveにまとめます。

## 2. 物理形式

- Extension: `.nbmp`
- Container: ZIP
- Required package manifest: `package_manifest.json`
- Required header path inside package: `song.nbmh`

## 3. Package Layout

```text
example.nbmp
  package_manifest.json
  song.nbmh
  audio.nbma
  score/
    main.nbmc
```

projectがmediaを参照する場合は、以下も含めます。

```text
media.nbmg
```

## 4. Package Manifest

```json
{
  "format": "NBMS-PACKAGE",
  "version": "0.1.0",
  "id": "converted.sample",
  "title": "BMS Sample",
  "header": "song.nbmh",
  "files": [
    "song.nbmh",
    "audio.nbma",
    "score/main.nbmc"
  ]
}
```

package manifestはNBMS headerの代替ではありません。installerやtool向けのpackage-level indexです。

## 5. Security

package integrityは `song.nbmh` に保存されるhashやsignatureに依存します。

署名済みNBMS projectをpackage化する場合、project fileを書き換えてはいけません。fileが変わった場合は、headerを再hashし、再署名する必要があります。

---

# NBMS Package `.nbmp` MVP

## 1. Purpose

`.nbmp` is the distribution package format for NBMS 0.1.

It bundles the minimum playable project files into one ZIP archive while preserving the logical three-file model used by players and editors.

## 2. Physical Format

- Extension: `.nbmp`
- Container: ZIP
- Required package manifest: `package_manifest.json`
- Required header path inside package: `song.nbmh`

## 3. Package Layout

```text
example.nbmp
  package_manifest.json
  song.nbmh
  audio.nbma
  score/
    main.nbmc
```

If the project references media:

```text
media.nbmg
```

is also included.

## 4. Package Manifest

```json
{
  "format": "NBMS-PACKAGE",
  "version": "0.1.0",
  "id": "converted.sample",
  "title": "BMS Sample",
  "header": "song.nbmh",
  "files": [
    "song.nbmh",
    "audio.nbma",
    "score/main.nbmc"
  ]
}
```

The package manifest is not a replacement for the NBMS header. It is only a package-level index for installers and tools.

## 5. Security

Package integrity relies on the hashes and signatures stored in `song.nbmh`.

Packaging a signed NBMS project must not rewrite project files. If any file is changed, the header must be rehashed and re-signed before packaging.

