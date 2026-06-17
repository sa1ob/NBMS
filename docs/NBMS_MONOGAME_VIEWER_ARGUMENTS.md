# NBMS MonoGame Viewer 起動引数

作成日: 2026-06-17

NBMS Studioから起動するMonoGame Viewerの現行引数一覧。

## 基本形式

```powershell
NBMS.Studio.MonoGameViewer.exe <header.nbmh> --chart <chartId> [options]
```

## 引数

- `<header.nbmh>`
  - 読み込むNBMSヘッダーファイル。
- `--chart <chartId>`
  - 再生する譜面ID。
- `--start-tick <tick>`
  - 指定tickからpreview開始する。
- `--end-tick <tick>`
  - 指定tickでrange previewを止める。
- `--ffmpeg <path>`
  - 使用する `ffmpeg.exe` を明示する。
  - 未指定時は `NBMS_FFMPEG_PATH`、Viewer横配置の `ffmpeg.exe`、PATH上の `ffmpeg` を試す。
- `--no-bga` / `-nobga`
  - `.nbmg` とBGA再生を無効化する。
- `--video-lead-ms <milliseconds>`
  - 動画BGAの表示補正値。
  - 範囲は `0` から `1500`。
  - 既定値は `360` ms。
  - WindowsMedia経路では動画decode/captureが遅れやすいため、少し先の動画位置を表示するために使う。

## Studio設定との対応

Studioは `%APPDATA%\NBMS Studio\settings.json` に以下を保存する。

```json
{
  "MonoGameViewerPath": "C:\\path\\to\\NBMS.Studio.MonoGameViewer.exe",
  "FfmpegPath": "C:\\path\\to\\ffmpeg.exe",
  "DisableBga": false,
  "VideoLeadMs": 360
}
```

## ffmpeg検出順

Viewer側の想定検出順:

1. `--ffmpeg <path>`
2. `NBMS_FFMPEG_PATH`
3. Viewer実行ファイルと同じフォルダの `ffmpeg.exe`
4. PATH上の `ffmpeg`
5. WindowsMedia fallback

Studio側の `Show ffmpeg status` は、上記に近い順序で現在使えそうなffmpegを表示する。

## 手動integration test

### ffmpegなし/OS codec fallback

- [ ] PATH上に `ffmpeg.exe` がないことを確認する。
- [ ] Studioのffmpeg設定を空、または存在しないパスにしない。存在しないパスは保存時に拒否される。
- [ ] MPEG/AVIなどWindowsMediaで開けるBGA付きNBMSを起動する。
- [ ] Viewerログに `decoder=WindowsMediaVideoDecoder` が出ることを確認する。
- [ ] Viewerが落ちず、動画またはエラー表示で継続することを確認する。

### ffmpegあり

- [ ] `ffmpeg.exe` のパスをStudioから設定する。
- [ ] `Show ffmpeg status` で検出パスが表示されることを確認する。
- [ ] BGA付きNBMSを起動する。
- [ ] Viewerログに `decoder=FfmpegVideoDecoder` が出ることを確認する。
- [ ] 動画が再生され、Viewerが落ちないことを確認する。

### unsupported codec

- [ ] WindowsMediaでもffmpegでも開けない、または壊れた動画を `.nbmg` に含むテストデータを用意する。
- [ ] Viewerがプロセスごと落ちないことを確認する。
- [ ] `%TEMP%\NBMS.Studio.MonoGameViewer.log` に `video failed` または `PlayVideo failed` が出ることを確認する。
- [ ] 画面上はBGAなし、または `BGA VIDEO ERROR / CHECK FFMPEG` として継続することを確認する。

