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
- 以前の音声キャッシュ済みPCM再生方式を外し、発音直前Reader方式をベースに整理
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
- `PlaybackSession` / `PlaybackTimelineMap` / `AudioScheduleEvent` を追加し、Viewer再生モデルをViewModelから分離
- `PlaybackLookaheadScheduler` を追加し、将来のaudio master clock / lookahead予約へ移行できる入口を作成
- Viewer上部に軽量debug overlayを追加し、イベントカーソル、asset数、tick、曲終端時刻を表示
- Viewer発音を100ms lookaheadへ変更し、音声Reader生成を発音予定時刻より前に開始
- `NbmsAudioPlayer.PlayOneShot(filePath, delaySeconds)` を追加し、残り待ち時間は `OffsetSampleProvider` の無音delayで調整
- 短音向けpreload cacheを追加し、8秒以下の使用音源は再生開始前にPCMへdecodeして発音時のReader生成を回避
- preload済み音源は `PreloadedSampleProvider` から再生し、長音やduration不明音源は従来のReader方式でstream寄りに扱う
- OGG/Vorbis音源はplayable noteで使われるduration不明音源のみpreload優先に変更し、長いBGMの無制限PCM展開を回避
- preload decodeに12秒上限を追加し、長い音源はstream fallbackへ戻す
- lookaheadを250msへ拡大し、Reader準備やpreload発音の余裕を増やした
- 同時発音時の割れ軽減のためOneShot音量を0.30へ下げた
- Viewerログに `route=preloaded` / `route=stream` を出し、発音経路を確認できるようにした
- `.ogg` / `.oga` は外部codecではなく `NAudio.Vorbis` でdecodeする方針を維持
- `DurationMs == 0` の音源は展開後の実ファイルからdurationを読み、PlaybackSessionの終端判定とpreload/stream分類へ反映
- OGG/OGAは `.nbma` 展開時に一時WAVへdecodeし、Viewer再生中はVorbis stream decodeを避けるよう変更
- AudioPlayerの `_gate` 保持中に `MixingSampleProvider.AddMixerInput` / `RemoveAllMixerInputs` / source disposeを呼ばないよう修正し、音声スレッドとのデッドロックを回避
- `AutoDisposeSampleProvider` / `PreloadedSampleProvider` のDisposeをスレッドセーフ化
- 先読み発音による曲頭スクロール/再生の崩れを避けるため、Viewerのlookaheadを0msへ戻した
- preload cacheは実装を残しつつ、再生開始時のpreload呼び出しを一旦停止
- `.nbmh` 読み込み時の音源展開/OGG一時WAV化を停止し、音源キャッシュ作成は再生開始時のみ行う
- `.nbmh` 読み込み後に音源キャッシュ作成をバックグラウンドで開始し、初回再生ボタン押下時の待ち時間を軽減

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
- 2P側チャンネル `#xxx21..29` とLN `#xxx61..69` を `key8..key14` / `scratch2` として変換
- 2P側チャンネルを検出した譜面は `beat-14k` として出力
- `#LNOBJ` 定義時に通常ノーツが消える問題を軽減するため、未確定LN開始候補を後続終端が来なければtapへ戻すよう修正
- Editor / Viewerの14keys表示では1P側と2P側の間にギャップを追加
- Viewerの14keys表示は7keys時のレーン幅を基準にプレイフィールド幅を広げるよう調整
- `.ogg` は変換時に再エンコードせず `audio.nbma` へそのまま格納
- manifest codecは `ogg-vorbis` として扱う
- Viewer再生に `NAudio.Vorbis` を追加
- `.ogg` は `VorbisWaveReader`、その他はMedia Foundationで読むよう分岐
- 既知問題: OGG音源が多い譜面ではbackground準備中に未準備音源へ到達すると一部発音が遅れる、または欠ける可能性がある

## 再生開始待ち時間の軽減

- プロジェクト読み込み直後の全音源キャッシュウォームアップを停止
- 再生開始時に `PlaybackSession.Events` から選択中譜面で使う `audioId` だけを抽出
- `NbmsAudioCache.Create` に必要 `audioId` フィルタを追加し、未選択譜面や未使用音源を展開しないよう変更
- OGG一時WAV変換も選択中譜面で必要な音源だけに制限
- Viewer上部に `Audio preparing...` の最小進捗表示を追加
- 選択譜面変更時は古い音源キャッシュを破棄し、次回再生時に新しい譜面用に準備し直す
- duration未設定音源はキャッシュ作成後にdurationを読めるため、音源準備後にPlaybackSessionを再構築するよう変更
- 再生開始時のblocking準備を曲頭約6秒分の音源に限定
- 曲頭以降の音源は再生開始後にbackgroundで準備
- background準備は譜面上の発音順を優先して処理
- 音割れ軽減のため、one-shot音量を下げ、出力段をsoft clipから簡易limiterへ変更
- OGG/Vorbis音源はキャッシュ作成時に一時WAVへ全展開せず、抽出したOGGファイルを直接stream再生する方式へ変更
- background準備を1音源単位に分割し、緊急準備が割り込みやすいよう調整
- 再生位置から約4秒以内に来る未準備音源を検出し、urgent準備として優先投入
- 発音時点でも未準備だった音源は、最後の保険として単体即時準備してから再生を試行
- OGG再生時の先頭ノイズ軽減のため、one-shot再生経路に約3msの短いfade-inを追加
- Viewerログは初期状態で非表示にし、ログ非表示時は従来通りログ出力も抑制
- 描画・発音イベントの基準時刻をStopwatch中心から音声出力のsample frame count由来へ変更
- ViewerのPlayheadTickと再生時刻表示は音声クロックを参照して更新
- 発音イベントのlookaheadを250msへ戻し、OGG Reader生成やdecode開始の遅延を吸収しやすく調整
- 曲頭の短音は再生開始前にPCM preloadし、background/urgentで準備した短音も抽出直後にpreloadするよう変更
- OGG大量譜面では、残課題として同時発音数が多い箇所のdecode/preload追従性をさらに検証する必要あり
- `PlaybackAssetPlan` を `ShortPcm` / `LongStream` に分類するよう変更
- Noteで使われる12秒以下またはduration不明の音源は `ShortPcm` としてPCM cache対象にする
- BGMや12秒超の長尺音源は `LongStream` としてstream routeを許可
- `ShortPcm` 音源は発音時にstream fallbackしないよう変更
- `ShortPcm` 発音時に未preloadの場合は、最後の保険として単体PCM preloadを試みる
- OGG音源でPCM preloadが間に合わない場合にobject音が無音になる問題を避けるため、`ShortPcm`未準備時は `emergency-stream` として救済再生するよう変更
- OGG短音のPCM decode失敗を減らすため、`ShortPcm`分類とpreload decode上限を12秒から30秒へ拡張
- OGG短音がある譜面では、曲頭だけでなくobjectに割り当てられた `ShortPcm` 音源全体を再生開始前のstartup cache/preload対象に変更
- OGG判定はmanifest codecだけでなく `.ogg` / `.oga` 拡張子でも行う

## MonoGame Viewer試作

- `NBMS.Studio.MonoGameViewer` プロジェクトを追加
- `MonoGame.Framework.WindowsDX` を利用した別プロセスViewerとして構成
- NBMSヘッダーと譜面IDを引数で受け取り、選択譜面を読み込む
- 高FPS game loopでレーン、小節線、object、判定線を描画
- 7keys / 14keysの基本レーン配置に対応
- Studio本体のメニューとツールバーに `MonoGame Viewer` 起動導線を追加
- 黒画面に見えやすい状態を避けるため、上部にピクセル文字の診断表示を追加
- 譜面ロード状態、note数、measure数、HiSpeed操作を画面内に表示
- レーン配色と外枠を明るめに調整し、譜面未ロード時でもプレイフィールドを視認しやすく変更
- 14keysの `scratch2` と `key8..key14` の表示位置をEditor/Viewer側のレーン順に合わせて修正
- Studioからの起動時は、build済み `NBMS.Studio.MonoGameViewer.exe` を優先して直接起動するよう変更
- README / Build手順にMonoGame Viewer projectのrestore/build/publishを追記
- StudioからMonoGame Viewerを起動する際、起動済みプロセスを保持して多重起動を抑制
- MonoGame Viewerのウィンドウが一定時間表示されない場合は、バックグラウンドに残ったプロセスを自動停止
- Studio終了時に起動中のMonoGame Viewerも閉じるよう変更
- MonoGame Viewer起動前例外を `%TEMP%\NBMS.Studio.MonoGameViewer.log` に出力
- MonoGame Viewerのentry pointを明示的な `[STAThread]` `Main` に変更
- MonoGame Viewerの通常起動経路でも `%TEMP%\NBMS.Studio.MonoGameViewer.log` に起動引数と初期化状態を記録
- Studio側のViewer起動監視を約6秒から約20秒へ延長し、DirectX/MonoGame初期化待ちを許容
- MonoGame Viewerのプロジェクト読み込みを軽量化し、音源manifest/hash検証を行わずヘッダーJSONと譜面JSONだけを読むよう変更
- MonoGame Viewerの初期化スレッドが `audio.nbma` hash計算で止まり、ウィンドウが表示されない問題を修正
- `MonoGame.Framework.WindowsDX` のWinForms土台を明示するため、MonoGame Viewer projectに `UseWindowsForms=true` を追加
- MonoGame ViewerにNAudio / NAudio.Vorbisを追加し、`.nbma` から必要音源を一時展開してone-shot再生する最小音声ルートを実装
- MonoGame Viewerの音声再生を「発音時にdecode」から「起動時にPCM preloadし、少し先の発音時刻へ予約投入」する方式へ変更
- WAV/OGGとも発音タイミングでreader生成・decodeを行わない経路を優先し、発音遅延とOGGノイズを軽減
- 同時発音時のクリップを抑えるため、MonoGame Viewer音声mixer出力へ簡易limiterを追加
- Viewer画面に譜面情報、発音ログ、直近object情報、combo表示を追加
- objectが判定ラインへ到達したタイミングでcombo加算と判定ライン発光エフェクトを表示
- 判定ライン発光エフェクトを全レーン表示からobjectが到達したレーンのみの表示へ変更
- MonoGame Viewerの `8` キーでobjectエフェクトON/OFF、`9` キーでログ表示ON/OFFを切り替え
- スクロール時刻を `GameTime` 加算から `Stopwatch` 基準へ変更し、フレーム揺れによるスクロールのガタつきを軽減
- 現時点では描画ループと最小音声再生の検証用で、audio clockを主時計にした厳密同期は未接続
- 判定ライン到達時のobjectエフェクトはいったん無効化し、`8` キーのeffect切り替え表示も削除
- MonoGame Viewerの矩形描画を整数 `Rectangle` 指定からfloat座標のSpriteBatch描画へ変更し、スクロール座標の整数丸めによるコマ送り感を軽減
- MonoGame Viewerに `7` キーで `LOCK60` / `LOCK120` / `UNLIMIT` を切り替えるFPS制限モードを追加し、画面内とWindow titleに現在FPSとモードを表示
- ログ非表示時はViewer内ログとファイルログの出力を抑制し、音数が多い譜面で発音イベントごとのI/Oがスクロールへ影響しないよう変更
- MonoGame Viewerの左情報パネルから直近objectの `OBJ ...` 表示を削除
- Studio本体のメニューとツールバーから旧Avalonia Viewer起動ボタンを非表示化し、EditorからのViewer起動導線をMonoGame Viewerに寄せる
- MonoGame Viewerの14keys表示時はCOMBO/FPS/AUDIO情報を上部ステータス帯へ移動し、レーンと重ならないよう調整
- MonoGame Viewerの14keysレーン配置を `scratch, key1..key7, key8..key14, scratch2` に変更し、2P側スクラッチを右端の赤レーンとして表示
- BMS変換時のheader BPM min/maxを譜面内のBPM timingイベントから再計算し、チャンネル03/08由来のBPM変化もmetadataへ反映
- CoreのTimelineServiceにBPM/STOP込みのtick秒変換APIを追加し、MonoGame Viewerのnote/BGM/小節線の秒位置計算を初期BPM推定から共通変換へ変更
- 旧PlaybackSessionの末尾tick推定も初期BPM固定ではなく、譜面末尾時点のBPMからticks/secを解決するよう変更

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
