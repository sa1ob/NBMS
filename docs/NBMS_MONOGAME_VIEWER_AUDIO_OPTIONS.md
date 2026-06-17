# NBMS MonoGame Viewer Audio Options

作成日: 2026-06-17

MonoGame Viewerの音声再生は、短音をPCM cacheへdecodeしてから鳴らす方針を基本にします。
音割れや密度の高い譜面での出力調整用に、以下の起動引数を用意しています。

```powershell
NBMS.Studio.MonoGameViewer.exe <header.nbmh> --chart <chartId> `
  --audio-volume 0.28 `
  --master-gain 0.82 `
  --limiter-threshold 0.90
```

## Options

- `--audio-volume <value>`
  - object音をMixerへ入れる前の音量です。
  - 範囲: `0.0` から `2.0`
  - 既定値: `0.28`
- `--master-gain <value>`
  - 出力直前、limiter前の全体gainです。
  - 範囲: `0.0` から `2.0`
  - 既定値: `0.82`
- `--limiter-threshold <value>`
  - 簡易limiterの上限値です。
  - 範囲: `0.1` から `1.0`
  - 既定値: `0.90`

## Studio Setting

NBMS Studioから起動する場合は、`MonoGame(Viewer)` ボタンのcontext menuから
`Set audio options...` を選ぶと以下を保存できます。

```json
{
  "AudioVolume": 0.28,
  "MasterGain": 0.82,
  "LimiterThreshold": 0.90
}
```

保存先は `%APPDATA%\NBMS Studio\settings.json` です。
通常preview、From tick、Range playのいずれでも同じ設定をMonoGame Viewerへ渡します。

## Tuning Notes

- 音が割れる場合は、まず `--master-gain` を `0.70` から `0.78` 程度へ下げて確認します。
- 全体の音が小さいだけの場合は、`--audio-volume` を先に調整します。
- `--limiter-threshold` は最終段の保護として扱い、極端に下げすぎない方針です。
- codec別の固定補正は行わず、PCM cache + master gain + limiterで共通に扱います。

## Current Implementation

- OGG/OGA: `VorbisWaveReader`
- WAV/FLAC/Media Foundation対応形式: `MediaFoundationReader`
- MonoGame Viewerは必要なaudioIdだけを `.nbma` から一時展開します。
- 展開後、decode可能な短音は `float[]` PCM cacheとして保持します。
- PCM cacheにない音はstream fallbackで再生します。
