# bms2nbms

Phase 2 prototype converter from BMS to NBMS 0.1.

This tool is intentionally small. It exists to prove the Phase 1 data model can be generated from existing BMS-like files.

## Supported Input

- Header fields: `#TITLE`, `#SUBTITLE`, `#ARTIST`, `#GENRE`, `#BPM`, `#PLAYLEVEL`
- Audio definitions: `#WAVxx filename`
- Extended BPM definitions: `#BPMxx value`
- STOP definitions: `#STOPxx value`
- Measure length changes: channel `02`
- Background audio: channel `01`
- BPM changes: channels `03` and `08`
- STOP events: channel `09`
- Basic playable note channels: `11` through `19`
- Basic long-note channels: `51` through `59`

Unsupported channels are skipped and reported as warnings.

## Usage

```text
python tools/bms2nbms/bms2nbms.py path/to/chart.bms out_dir
```

Output:

```text
out_dir/
  song.nbmh
  score/
    main.nbmc
  audio.nbma
```

The generated `.nbma` is a ZIP archive with a `manifest.json` and audio files.

By default, PCM16 WAV files are converted to FLAC before being stored. Use `--no-flac-transcode` to keep source audio as-is for debugging.

## Codec Note

NBMS 0.1 requires FLAC playback. This prototype includes a small built-in WAV PCM16 to FLAC converter that writes verbatim-subframe FLAC streams. It is suitable for validating the NBMS container and playback flow; a production tool should later switch to a mature codec implementation.

Non-WAV and non-FLAC files are still packaged as-is and reported as warnings.

---

# bms2nbms 日本語版

BMSからNBMS 0.1へ変換するPhase 2プロトタイプです。

このtoolは、既存BMS系ファイルからPhase 1のdata modelを生成できるか検証するための小さな実装です。

## 対応入力

- Header fields: `#TITLE`, `#SUBTITLE`, `#ARTIST`, `#GENRE`, `#BPM`, `#PLAYLEVEL`
- Audio definitions: `#WAVxx filename`
- Extended BPM definitions: `#BPMxx value`
- STOP definitions: `#STOPxx value`
- Measure length changes: channel `02`
- Background audio: channel `01`
- BPM changes: channels `03` and `08`
- STOP events: channel `09`
- Basic playable note channels: `11` through `19`
- Basic long-note channels: `51` through `59`

未対応channelはskipし、warningとして報告します。

## 使い方

```text
python tools/bms2nbms/bms2nbms.py path/to/chart.bms out_dir
```

出力:

```text
out_dir/
  song.nbmh
  score/
    main.nbmc
  audio.nbma
```

生成される `.nbma` は、`manifest.json` と音声ファイルを含むZIP archiveです。

既定ではPCM16 WAVをFLACへ変換して格納します。debug用途でsource audioをそのまま保持したい場合は `--no-flac-transcode` を使います。

## Codecメモ

NBMS 0.1ではFLAC playbackを前提候補としています。このprototypeには、verbatim-subframe FLACを書き出す簡易WAV PCM16 to FLAC converterが含まれています。production toolでは成熟したcodec libraryへ置き換えるべきです。

WAV / FLAC以外のfileはそのままpackageされ、warningとして報告されます。
