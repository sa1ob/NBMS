# NBMS公開用Editor/Viewer一体アプリ仕様検討

## 1. 目的

NBMSフォーマットは、現時点ではベースルールを定めた段階である。

次の公開ステップでは、フル機能の制作環境や本格ゲームプレイヤーをいきなり作るのではなく、以下を満たす最小公開版を作る。

- NBMSファイルを開ける
- NBMS譜面を視覚的に確認できる
- NBMS譜面を最低限編集できる
- NBMSファイルを保存できる
- 既存BMSから最小NBMSへ変換できる
- 通常形式/暗号化形式を選んで保存できる
- 配布用 `.nbmp` を作成できる
- 今後のRANDOM、STOP、BGA、SCROLLなどをモジュールで拡張できる

この文書では、公開用アプリを `NBMS Studio` と仮称する。

## 2. 前提

これまでに検討・試作したNBMSの基本構成は以下である。

```text
song.nbmh    # Header JSON
chart.nbmc   # Chart JSON
audio.nbma   # Audio archive
media.nbmg   # Optional media archive
```

配布パッケージ:

```text
song.nbmp    # ZIP based package
```

現時点での基本方針:

- ヘッダーはJSON固定
- 譜面はJSON固定
- 音源パックはZIPベース `.nbma`
- 必須音声コーデックはFLAC
- 譜面ハッシュは正規化JSON + SHA-256
- 作者署名はEd25519
- 音声ペイロード暗号化はChaCha20-Poly1305またはAES-GCMを候補
- BGA/動画/画像は `.nbmg` として音源とは別パック

## 3. 公開版で守るべき方針

### 3.1 いきなり全部を定義しない

NBMSはまだベースルール段階であるため、最初の公開版では以下を避ける。

- RANDOMや分岐仕様をコア仕様に固定する
- STOP/SCROLL/BGAなどをすべてコアに詰め込む
- 譜面バイナリ形式を早期確定する
- 暗号化・鍵配布モデルを最終仕様として断言する

代わりに、ベースルールと拡張ルールを分離する。

### 3.2 CoreとExtensionを分ける

Coreは、どのNBMS実装でも必ず理解すべき最小仕様とする。

Core:

- ファイル構成
- ヘッダー読み込み
- 譜面読み込み
- 音源パック読み込み
- `audioId` 解決
- 基本ノーツ
- 基本BPM
- 小節線
- 譜面ハッシュ
- パッケージ読み書き

Extension:

- STOP
- SCROLL
- RANDOM
- BGA/動画/画像
- 譜面分岐
- LNの細かい解釈差
- 判定拡張
- スキン/レーン表現
- 暗号化方式の追加
- 追加音声コーデック

### 3.3 EditorとViewerは一体化する

公開版では、EditorとViewerを別アプリにしない。

理由:

- NBMSを確認する人と編集する人の導線を短くする
- 仕様検証時に「編集してすぐ見る」が重要
- Viewerだけ先に作ると編集ワークフローの問題を見落としやすい
- Editorだけ先に作ると再生・タイミング確認の問題を見落としやすい

アプリ内では、以下のモードを切り替える。

- Library/Project
- Metadata
- Chart Table
- Timeline Viewer
- Audio
- Package/Export
- Extensions

## 4. NBMS Studio MVP機能

### 4.1 読み込み

- フォルダ形式のNBMSを開く
- `.nbmp` パッケージを開く
- `.nbmh` から参照譜面と音源パックを解決する
- `.nbma` の `manifest.json` を読む
- 譜面ハッシュを検証する
- 音源パックハッシュを検証する
- 署名がある場合は検証する
- 暗号化音源の場合は復号用パスフレーズまたは鍵を要求する

### 4.2 Viewer

- 譜面をタイムライン表示する
- ノーツをレーン別に表示する
- BPM/小節線を表示する
- 背景音声イベントを表示する
- 音源参照切れを表示する
- 再生カーソルを動かす
- 音声を再生する
- 再生中にノーツ位置を確認できる

MVPでは、本格的なゲームプレイ判定は不要とする。

### 4.3 Editor

- ヘッダー情報編集
- 譜面一覧編集
- ノーツ表編集
- タイムライン上での簡易ノーツ追加/移動
- `audioId` の選択
- 音源一覧表示
- 参照切れチェック
- 保存
- 別名保存
- 暗号化コピー保存

MVPのノーツ編集は、以下の2段階でよい。

1. 表形式編集
2. タイムラインでの簡易編集

本格的なピアノロールやキーボード入力エディタは次段階でよい。

### 4.4 BMS変換

- 既存BMSを開く
- 最小NBMSへ変換する
- WAVをFLACへ変換する
- `.nbma` を作る
- `.nbmh` と `.nbmc` を作る
- 変換後にそのままEditorで開く

初期対応チャンネル:

- `#TITLE`
- `#ARTIST`
- `#GENRE`
- `#BPM`
- `#PLAYLEVEL`
- `#WAVxx`
- 基本ノーツ
- 背景音声
- BPM変更
- STOP

未対応チャンネルは警告として表示する。

### 4.5 作者鍵管理

- 作者鍵を生成する
- 作者秘密鍵を読み込む
- 公開鍵を書き出す
- 保存時に署名する/しないを選択する
- 通常形式で保存する
- 暗号化形式で保存する

重要:

- 暗号化は任意
- 署名も任意
- 署名なし/暗号化なしのオープンなNBMSを作れる
- 暗号化形式では、音源ペイロードを暗号化する
- ヘッダーは検索性のため原則平文にする

### 4.6 配布パッケージ作成

- `.nbmp` を作成する
- `package_manifest.json` を含める
- `song.nbmh`、譜面、`.nbma`、任意の `.nbmg` を含める
- パッケージ作成前にハッシュ検証を行う
- 署名済みの場合は署名検証を行う
- 暗号化形式の場合は暗号化済み `.nbma` を含める

## 5. 拡張可能なフォーマット設計

### 5.1 Extension Manifest

ヘッダーまたは譜面に、使用している拡張を明示する。

例:

```json
{
  "extensions": [
    {
      "id": "nbms.stop",
      "version": "0.1.0",
      "required": true
    },
    {
      "id": "nbms.random",
      "version": "0.1.0",
      "required": false
    }
  ]
}
```

`required: true` の拡張に未対応の場合、Viewer/Editorは読み込みを拒否または互換モードで開く。

`required: false` の拡張に未対応の場合、警告を出して無視できる。

### 5.2 譜面イベントの拡張

Coreイベント:

```json
{
  "tick": 0,
  "type": "bpm",
  "value": 150
}
```

Extensionイベント:

```json
{
  "tick": 3840,
  "type": "extension",
  "extensionId": "nbms.random",
  "event": "branch",
  "payload": {
    "seed": 1234,
    "groups": ["A", "B"]
  }
}
```

ただし、頻出する公式拡張は以下のように直接 `type` を持たせてもよい。

```json
{
  "tick": 7680,
  "type": "stop",
  "durationTicks": 480
}
```

この場合も、処理するモジュールは `nbms.stop` として分離する。

### 5.3 モジュール境界

アプリ内では、以下のモジュール境界を定義する。

- Format Core
- Chart Model
- Audio Pack
- Timeline Engine
- Renderer
- Audio Engine
- Extension Host
- Editor UI
- Package/Export
- Crypto/Signature

拡張モジュールは、Coreに直接依存しすぎないようにする。

## 6. アプリ内部アーキテクチャ案

```text
NBMS Studio
  app-shell
  core-format
  chart-model
  audio-pack
  timeline-engine
  audio-engine
  renderer
  editor-ui
  extension-host
  extensions/
    stop
    scroll
    random
    bga
  tools/
    bms-import
    package-export
    crypto
```

### 6.1 Core Format

責務:

- `.nbmh` 読み書き
- `.nbmc` 読み書き
- `.nbma` 読み込み
- `.nbmp` 展開/作成
- JSON Schema検証
- 正規化ハッシュ

### 6.2 Chart Model

責務:

- 譜面を内部モデルへ変換
- ノーツ一覧を管理
- タイミングイベント一覧を管理
- 編集操作をUndo/Redo可能なコマンドとして扱う

### 6.3 Timeline Engine

責務:

- tickを時刻へ変換
- BPM変更を処理
- STOPなどの拡張タイミングをExtensionへ委譲
- ViewerとAudio Engineへ再生スケジュールを渡す

### 6.4 Extension Host

責務:

- 拡張モジュールを登録する
- 拡張イベントを解釈する
- 未対応拡張を検出する
- Editor UIに拡張用パネルを追加する

想定インターフェース:

```text
ExtensionModule
  id
  version
  supportedEventTypes
  validate(event)
  applyTiming(context, event)
  renderOverlay(context, event)
  editorPanel(context)
```

## 7. 実装言語・フレームワーク候補

要件:

- Windows環境で環境差が少ない
- 単一exeで配布しやすい
- EditorとViewerを一体化できる
- 音声再生、描画、ファイルI/O、暗号、ZIP、JSONを扱いやすい
- 拡張機能をモジュール化しやすい

## 8. 候補A: C# / .NET 8 / WPF

### 概要

Windows向けデスクトップアプリとして堅実な選択肢。

### Pros

- Windowsとの相性が非常に良い
- 単一exe配布が可能
- WPFで業務ツール的なEditor UIを作りやすい
- TreeView、DataGrid、PropertyGrid的UIが作りやすい
- JSON、ZIP、暗号、署名の標準/成熟ライブラリが豊富
- プラグイン構造をAssembly単位で作りやすい
- 長期保守しやすい

### Cons

- 高性能なリアルタイム描画は工夫が必要
- 音声低遅延再生は別ライブラリが必要になりやすい
- WPFの見た目が古くなりやすい
- クロスプラットフォーム性は弱い

### 使う候補ライブラリ

- UI: WPF
- Audio: NAudio
- FLAC: NAudio/Vorbis系連携またはFLAC decoderライブラリ
- JSON: System.Text.Json
- ZIP: System.IO.Compression
- Crypto: System.Security.Cryptography
- Plugin: AssemblyLoadContext

### 評価

Windows単一exeという条件にはかなり強い。

Editor機能中心なら最有力。ただし、Viewerの描画や音声同期をどこまで作り込むかで追加設計が必要。

## 9. 候補B: C# / .NET 8 / Avalonia

### 概要

WPFより現代的で、クロスプラットフォームも視野に入るUIフレームワーク。

### Pros

- Windows単一exe配布が可能
- UIがWPFより新しく作りやすい
- DataGridなどEditor向けUIがある
- 将来Linux/macOS対応も見える
- MVVM構成に向く
- .NETのライブラリ資産を使える

### Cons

- WPFよりWindowsネイティブ感は薄い
- 一部UI部品や挙動で追加調整が必要
- WPFほど企業向け実績が多くない
- 音声・描画部分は別途設計が必要

### 使う候補ライブラリ

- UI: Avalonia
- Audio: NAudio
- JSON: System.Text.Json
- ZIP: System.IO.Compression
- Crypto: System.Security.Cryptography
- Plugin: AssemblyLoadContext

### 評価

公開用アプリとして見た目と保守性を両立しやすい。

Windows以外の将来も残したいならWPFより良い。

## 10. 候補C: Rust / Tauri / Web UI

### 概要

Rustをバックエンド、HTML/CSS/TypeScriptをUIに使う構成。

### Pros

- 単一exeに近い配布が可能
- UIをWeb技術で作れる
- タイムラインや表編集を作りやすい
- Rust側でフォーマット処理・暗号・ZIPを堅牢に実装できる
- モジュール境界を明確に設計しやすい
- アプリサイズがElectronより小さい

### Cons

- Windows WebView2依存がある
- 完全な「どのWindowsでも単体で必ず動く」とは言いづらい
- Rust + TypeScriptの二重構成になる
- 音声低遅延再生は設計難度が上がる
- 開発者の負荷が高い

### 使う候補ライブラリ

- UI: Tauri + TypeScript
- Timeline: Canvas/WebGL
- Audio: WebAudioまたはRust側音声ライブラリ
- JSON: serde
- ZIP: zip crate
- Crypto: ring / ed25519-dalek / chacha20poly1305
- Plugin: Rust trait + WASM/JS extension

### 評価

UI表現力と拡張性は高いが、最初の公開版には少し重い。

WebView2依存を許容できるなら有力。

## 11. 候補D: C++ / Qt 6

### 概要

本格的なデスクトップアプリとして強い選択肢。

### Pros

- 高性能
- 音声・描画・UIを一体で作りやすい
- Windows単一配布も可能
- 複雑なEditor UIに強い
- プラグイン構造も作れる

### Cons

- ビルド・配布が重い
- 開発コストが高い
- C++の安全性と保守性に注意が必要
- JSON/暗号/音声/FLACなどの依存管理が面倒

### 使う候補ライブラリ

- UI: Qt Widgets/QML
- Audio: Qt MultimediaまたはPortAudio
- JSON: Qt JSON
- ZIP: QuaZip/miniz
- Crypto: libsodium/OpenSSL
- Plugin: Qt plugin system

### 評価

将来本格DAW的なEditorにするなら強いが、初期公開版には重い。

## 12. 候補E: Python / PySide6 / PyInstaller

### 概要

現在の試作資産を活かしやすい選択肢。

### Pros

- 既存のPython試作を流用しやすい
- GUIを比較的早く作れる
- PyInstallerでexe化できる
- JSON/ZIP処理が簡単
- 開発速度が速い

### Cons

- 単一exeが大きくなりやすい
- 起動が遅くなりやすい
- 環境差を完全に消しにくい
- 音声再生や低遅延再生で苦労しやすい
- 公開用の信頼感・保守性はC#やRustより弱い

### 使う候補ライブラリ

- UI: PySide6
- Audio: sounddevice / miniaudio / ffmpeg同梱
- JSON: 標準json
- ZIP: 標準zipfile
- Crypto: cryptography
- Packaging: PyInstaller

### 評価

検証版を早く公開するには良い。

ただし、Windows環境差の少ない単一exeを目指すなら、長期本命にはしづらい。

## 13. 候補F: Godot

### 概要

Viewer寄り、再生・描画寄りのアプリとしては面白い選択肢。

### Pros

- Windows exe配布しやすい
- タイムライン描画や再生画面を作りやすい
- 音声再生が組み込まれている
- UIも最低限作れる
- プラグイン/スクリプトで拡張できる

### Cons

- JSON編集・表編集などEditor UIは作りづらい
- 業務ツール的なファイル管理UIに弱い
- 暗号やZIPなどの実装は追加対応が必要
- フォーマット制作ツールとしては少しゲームエンジン寄りすぎる

### 評価

Viewer優先なら候補に入るが、Editor/Viewer一体の制作ツールとしては第一候補ではない。

## 14. 推奨方針

現時点の要件では、第一候補は以下とする。

```text
C# / .NET 8 / Avalonia
```

理由:

- Windows単一exe配布が可能
- Editor向けUIを作りやすい
- Viewerの描画も十分可能
- .NETのJSON/ZIP/暗号ライブラリが使える
- プラグイン/モジュール化がしやすい
- 将来クロスプラットフォーム対応も残せる
- WPFより現代的なUIにしやすい

Windows専用に割り切る場合の第二候補:

```text
C# / .NET 8 / WPF
```

短期検証版を最速で出す場合の暫定候補:

```text
Python / PySide6 / PyInstaller
```

ただし、公開して継続開発する前提なら、Python版はプロトタイプまでに留め、正式公開版はC#へ移行するのが望ましい。

## 15. 推奨アーキテクチャ

```text
NBMS.Studio
  NBMS.Core
  NBMS.Audio
  NBMS.Crypto
  NBMS.Import.Bms
  NBMS.Extensions
  NBMS.Extensions.Stop
  NBMS.Extensions.Random
  NBMS.Extensions.Bga
  NBMS.Editor
  NBMS.Viewer
  NBMS.Packaging
```

### 15.1 NBMS.Core

- Header model
- Chart model
- Audio manifest model
- Package manifest model
- JSON Schema validation
- Canonical JSON hash
- Version negotiation

### 15.2 NBMS.Extensions

- Extension registry
- Extension capability check
- Event parser dispatch
- Editor panel registration
- Viewer overlay registration
- Timeline transform hook

### 15.3 NBMS.Editor

- Project tree
- Metadata editor
- Chart table editor
- Audio list
- Reference check
- Export UI

### 15.4 NBMS.Viewer

- Timeline renderer
- Playback cursor
- Lane renderer
- Event renderer
- Basic audio playback

### 15.5 NBMS.Import.Bms

- BMS parser
- WAV/FLAC conversion
- Channel mapping
- Warning report

## 16. Extension API方針

初期公開版では、外部DLLプラグインまで許可しなくてよい。

まずはアプリ内モジュールとして拡張を分離する。

段階:

1. Built-in extension modules
2. Internal extension registry
3. Experimental external plugin API
4. Stable external plugin API

初期実装では以下をBuilt-in extensionとして扱う。

- `nbms.stop`
- `nbms.scroll`
- `nbms.bga`

RANDOMは仕様が複雑化しやすいため、最初は読み込み警告のみ、または実験拡張として扱う。

## 17. 初期公開版の画面案

### 17.1 Project

- 開いている曲情報
- 譜面一覧
- 音源パック情報
- 署名/暗号化状態
- 検証結果

### 17.2 Metadata

- title
- artist
- genre
- bpm
- rights
- license

### 17.3 Chart

- 譜面選択
- ノーツ表
- タイミングイベント表
- 参照切れ警告

### 17.4 Timeline Viewer

- レーン表示
- ノーツ表示
- 再生カーソル
- BPM/小節線
- ズーム
- 再生/停止

### 17.5 Audio

- `audioId`
- codec
- duration
- rights
- used/unused
- preview playback

### 17.6 Export

- 通常保存
- 署名保存
- 暗号化保存
- `.nbmp` 作成
- BMSから変換

## 18. 実装順

### Step 1: C#ソリューション作成

- `NBMS.Core`
- `NBMS.Studio`
- `NBMS.Tests`

### Step 2: Core移植

- `.nbmh` 読み込み
- `.nbmc` 読み込み
- `.nbma` manifest読み込み
- `.nbmp` 展開/作成
- ハッシュ計算

### Step 3: Viewer

- 譜面タイムライン表示
- tick to time変換
- 簡易再生カーソル
- 音声プレビュー

### Step 4: Editor

- Metadata編集
- ノーツ表編集
- 音源一覧
- 参照切れチェック
- 保存

### Step 5: Export

- 通常保存
- 署名保存
- 暗号化保存
- `.nbmp` 作成
- BMS変換

### Step 6: Extension Host

- STOPをモジュール化
- SCROLLをモジュール化
- BGAをモジュール化
- RANDOMは実験拡張として設計のみ置く

## 19. 公開時に含めるもの

- `NBMS Studio.exe`
- サンプルNBMS
- 仕様ドラフト
- README
- 既知の制限
- 変換対応BMS機能一覧
- 拡張未対応時の挙動説明

単一exe配布を重視する場合、基本は単一exeとし、サンプルや仕様書は別ZIPで配布する。

## 20. 結論

公開版は、次の方針が最も現実的である。

```text
C# / .NET 8 / Avalonia で Editor/Viewer 一体型の NBMS Studio を作る
```

初期公開版では、NBMSのCore仕様を安定させることを最優先する。

RANDOM、STOP、BGAなどは、Coreに直接埋め込まず、Extension Moduleとして実装する。

最初から外部プラグインAPIを公開するのではなく、まず内部モジュール境界を明確にし、その後に外部拡張APIを公開する。

これにより、NBMSフォーマット自体を硬直化させず、Editor/Viewerを公開しながら仕様を育てられる。

