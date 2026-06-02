# nbms_pack

Create a distributable `.nbmp` package from an NBMS header.

```text
python tools/nbms_pack/nbms_pack.py examples/converted_phase4/song.nbmh dist/sample.nbmp
```

The package is a ZIP archive containing:

- `package_manifest.json`
- `song.nbmh`
- all chart files referenced by the header
- the `.nbma` audio archive
- the optional `.nbmg` media archive if referenced and present

---

# nbms_pack 日本語版

NBMS headerから配布用 `.nbmp` packageを作成します。

```text
python tools/nbms_pack/nbms_pack.py examples/converted_phase4/song.nbmh dist/sample.nbmp
```

packageはZIP archiveで、以下を含みます。

- `package_manifest.json`
- `song.nbmh`
- headerが参照するすべてのchart file
- `.nbma` audio archive
- optionalな `.nbmg` media archive
