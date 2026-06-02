# NBMS Studio 実装状況

最終更新: 2026-06-02

このファイルは、NBMS Studio の実装状況です


# 既知の問題
- スクロールが不安定
- oggの場合再生が不安定
- 変換できないBMSが多くある
- 14Keys等非対応
- 地雷、LNなども非対応

## 実装済み

- `NBMS.Core` プロジェクトを作成
- `NBMS.Studio.App` Avalonia プロジェクトを作成
- `.nbmh` / `.nbmc` / `.nbma manifest.json` のモデル定義
- NBMSプロジェクト読み込みサービス
- 譜面ハッシュと音源パックハッシュの検証
- 音源参照切れチェック
- BPM / STOP を考慮するTimeline生成
- built-in拡張モジュール登録基盤
- `.nbmp` パッケージ作成サービス
- BMSインポートサービスの基礎実装
- Avalonia UI の Editor / Viewer 一体型アプリ
- NBMSヘッダー読み込み
- メタデータ編集
- 譜面表形式編集
- 音源一覧表示
- 参照切れチェック表示
- `.nbmp` パッケージ作成

## Editor

- メニュー: ファイル / 編集 / 表示 / 設定 / 外部Viewer / ヘルプ
- ツールバー: 開く / BMS一括変換 / 保存 / `.nbmp` 作成 / Viewer
- 左ペイン: プロジェクト情報、譜面選択、譜面一覧、音源一覧
- 中央ペイン: 縦型Timeline Viewer / Editor
- Timelineの表示順は `scratch, key1, key2, key3, key4, key5, key6, key7, background1..background8`
- スクラッチは赤、1/3/5/7鍵は白、2/4/6鍵は青で表示
- BGM / 自動再生音源は `background1..background8` として右側に表示
- Timelineのtick-to-pixel変換を `0.125px/tick` に統一
- 1小節3840tickが480pxになるよう固定
- 小節線、Timing線、object表示で同じ座標基準を使用
- object矩形のY座標を整数pxへ丸め、スクロール中のにじみやズレを軽減

## 複数譜面選択

- Editor左ペインの譜面情報上に `ComboBox` を追加
- header内の `charts` から編集/再生対象の譜面を選択可能
- 下部の譜面一覧DataGridも同じ選択状態へバインド
- ComboBox / DataGrid のどちらから選んでも対象譜面が同期
- 譜面切り替え時は現在のノート表編集内容を反映してから、Notes / Timeline / Viewer対象を切り替える

## Viewer / 再生

- `PlaybackWindow` を追加
- メイン画面の `Viewer` から別Windowを開くワークフローへ統一
- 編集画面のTimelineは再生位置スクロールと連動しない
- Viewerはプレイフィール寄りの `PlayfieldCanvas` を使用
- 再生ボタン、停止ボタン、最初に戻るボタンを配置
- 停止ボタンは一時停止として扱い、再生ボタンで停止位置から再開
- Viewerを閉じた場合は強制停止
- HiSpeed `x1.0` / `x1.5` / `x2.0` / `x3.0` / `x4.0` を追加
- HiSpeedは音声速度ではなくobjectの表示間隔のみ変更
- Viewer上端/下端の外にobjectが描画されないようクリップ判定を調整
- 曲末尾では最後の音を途中で切らないよう、Timeline終了時にミキサーを強制停止しない

## 再生安定化

- DispatcherTimerで再生位置を確認し、発音時刻を過ぎたイベントを即時OneShot再生する
- 音声キャッシュ済みPCM / OffsetSampleProvider方式を外した
- MediaFoundationReaderを発音ごとに開く安定寄りの実装へ戻した
- 同時発音時の破綻を抑えるため、OneShot音量を控えめにした
- 過大入力時のみソフトクリップする形へ調整

## Viewerスクロール最適化

- 再生タイマー間隔を16msへ変更し、おおむね60fps相当に調整
- 再生位置テキストは50ms間隔で更新し、レイアウト負荷を軽減
- 現在tick推定を線形走査から二分探索へ変更
- PlayfieldCanvasはTimeline行をコレクション変更時だけキャッシュ
- 描画時は画面内tick範囲だけを処理
- Viewer描画でBrush/Penを毎フレーム生成しないよう静的インスタンスを再利用

## BMS変換

- `ファイル > BMSフォルダから一括変換...` とツールバーの `BMS一括変換` を追加
- 入力フォルダ直下の `.bms` / `.bme` / `.bml` / `.pms` を一括変換
- フォルダ変換では出力ルートに `song.nbmh` と `audio.nbma` を1つだけ作成
- 複数譜面は `score/*.nbmc` として並べる
- `song.nbmh` の `charts` 配列へ、変換した全譜面の参照とハッシュをまとめて登録
- 音源は各BMSの `#WAVxx` 参照を共通 `audio.nbma` に集約
- audioId衝突時は安定した短いハッシュ接尾辞で回避
- 単体BMS変換も `score/main.nbmc` を出力

## BMS拡張定義とOGG対応

- `#xxx02` の小節長変更を読み、変換後NBMSのtick位置へ反映
- `#BPMxx` と `#xxx08`、旧式の `#xxx03` BPM変更をTiming `bpm` として出力
- `#STOPxx` と `#xxx09` をTiming `stop` として出力
- STOPはTimeline / 再生側の時間計算へ反映
- `#51-#59` の基本的なロングノートチャンネルを `hold` ノートへ変換
- `#LNOBJ` 形式の終端オブジェクトを最小対応
- `.ogg` は変換時に再エンコードせず `audio.nbma` へそのまま格納
- manifest codecは `ogg-vorbis` として扱う
- Viewer再生に `NAudio.Vorbis` を追加
- `.ogg` は `VorbisWaveReader`、その他はMedia Foundationで読むよう分岐

## まだ未実装・未検討

- C#側のWAV to FLAC変換
- 署名と暗号化保存
- `.nbmp` を直接開く処理
- Undo / Redo
- Timeline上でのマウス編集
- 判定、入力、ゲージ、スコア
- 高精度なオーディオミキサー
- 再生位置シーク
- 暗号化音源の復号再生
- BMS変換時のRANDOM / IF / SWITCH
- BGA / BMP / AVI / media対応
- mine note / invisible note
- 詳細なLN方言対応
- レーン幅、グリッド、スナップ、表示倍率の設定
- 外部拡張DLL読み込み
- RANDOM / BGAなどの追加拡張

## build手順

通常のWindows環境では以下を使用します。

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```
