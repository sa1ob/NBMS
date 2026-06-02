# BMS次世代フォーマット仕様検討ドラフト

## 1. 目的

BMS(Be-Music-Script)の既存資産や文化を尊重しつつ、次の問題を解決する新しい譜面フォーマットを検討する。

- 既存BMSの音数制限
- 多数のWAVファイルを直接扱うことによるディスクI/O増大
- 音素材がそのまま配布されることによる制作者・音源開発者の権利侵害リスク
- 譜面や音源の改ざん、無断編集、再配布への対策不足

次世代フォーマットでは、最低構成を以下の3ファイルとする。

- ヘッダーファイル: 曲情報、作者情報、譜面一覧、権利情報、署名情報
- 譜面ファイル: ノーツ、BPM、停止、レーン、演出、分岐などの譜面データ
- 音ファイル: 音声素材をまとめた音源パック

仮称として、この文書では新フォーマットを `NBMS` と呼ぶ。

## 2. 基本方針

### 2.1 BMSとの互換性方針

完全な後方互換ではなく、変換可能性を重視する。

- 既存BMSからNBMSへ変換できること
- 既存のBMS的なゲームプレイを表現できること
- 既存仕様の制限、特に `#WAVxx` のような短い識別子上限に縛られないこと
- 拡張仕様を前提にした譜面でも、プレイヤーが未対応機能を検出しやすいこと

### 2.2 ファイル形式の方針

人間が編集する領域と、機械的に扱う領域を分ける。

- ヘッダーは可読性と実装容易性を優先し、JSONに固定する
- 譜面はJSONに固定する。バイナリ形式は将来の高速化候補として後で検討する
- 音ファイルは単一のコンテナとし、内部にインデックス、音声データ、メタデータ、暗号化情報を持つ
- 音源パックは、実装しやすく要件に合う既存コンテナがあれば利用する

MVPでは、実装しやすさを優先して以下を推奨する。

- ヘッダー: JSON
- 譜面: JSON
- 音ファイル: 既存コンテナまたは最小独自コンテナ

将来的に譜面JSONをCBOR/MessagePack化しても、論理構造は維持する。

## 3. 推奨ファイル構成

最小構成:

```text
song.nbmh   # Header
chart.nbmc  # Chart
audio.nbma  # Audio archive
```

複数譜面を含む場合:

```text
song.nbmh
charts/
  beginner.nbmc
  hyper.nbmc
  another.nbmc
audio.nbma
media.nbmg  # Optional media archive
```

パッケージ配布する場合:

```text
example_song.nbmp
```

`.nbmp` はヘッダー、譜面、音源パックをZIPまたは独自コンテナにまとめた配布用パッケージとする。プレイヤー内部では3ファイル構成として扱えるようにする。

BGA、動画、画像などのメディア素材は音源パックとは分離し、`.nbmg` メディアパックとして扱う。メディア素材が存在しない曲では `.nbmg` は省略可能とする。

## 4. ヘッダーファイル仕様案

拡張子: `.nbmh`

役割:

- 曲全体の基本情報
- 譜面ファイル一覧
- 音ファイル参照
- 作者、権利、ライセンス情報
- 対応フォーマットバージョン
- 暗号化、署名、改ざん検出情報

例:

```json
{
  "format": "NBMS",
  "version": "0.1.0",
  "id": "com.example.song.sample",
  "title": "Sample Song",
  "subtitle": "",
  "artist": "Composer Name",
  "genre": "Artcore",
  "bpm": {
    "initial": 150.0,
    "min": 75.0,
    "max": 300.0
  },
  "preview": {
    "audioId": "preview",
    "startMs": 0,
    "durationMs": 30000
  },
  "audio": {
    "file": "audio.nbma",
    "hash": "sha256-..."
  },
  "media": {
    "file": "media.nbmg",
    "hash": "sha256-...",
    "optional": true
  },
  "charts": [
    {
      "id": "another",
      "file": "charts/another.nbmc",
      "mode": "beat-7k",
      "difficulty": 12,
      "levelName": "Another",
      "hash": "sha256-...",
      "hashAlgorithm": "sha256-canonical-json"
    }
  ],
  "rights": {
    "music": "Composer Name",
    "chart": "Chart Author",
    "soundSource": "Sound Developer",
    "license": "All rights reserved",
    "contact": "https://example.com"
  },
  "security": {
    "signed": true,
    "signatureAlgorithm": "Ed25519",
    "publicKeyId": "author-key-2026",
    "signature": "base64..."
  }
}
```

## 5. 譜面ファイル仕様案

拡張子: `.nbmc`

役割:

- ノーツ配置
- BPM変化
- STOP/SCROLLなどのタイミング制御
- レーン定義
- 音声イベント参照
- BGA/動画/画像などのメディアイベント参照
- 譜面固有メタデータ

### 5.1 時間表現

BMSの小節分割に加えて、内部的には高精度な絶対tickを持つ。

推奨:

- `resolution`: 1拍あたりのtick数
- `tick`: 曲開始からの絶対tick
- 小節線はイベントとして表現

例:

```json
{
  "format": "NBMS-CHART",
  "version": "0.1.0",
  "chartId": "another",
  "mode": "beat-7k",
  "resolution": 960,
  "lanes": [
    { "id": "scratch", "type": "scratch", "index": 0 },
    { "id": "key1", "type": "key", "index": 1 },
    { "id": "key2", "type": "key", "index": 2 },
    { "id": "key3", "type": "key", "index": 3 },
    { "id": "key4", "type": "key", "index": 4 },
    { "id": "key5", "type": "key", "index": 5 },
    { "id": "key6", "type": "key", "index": 6 },
    { "id": "key7", "type": "key", "index": 7 },
    { "id": "background1", "type": "background-audio", "index": 8, "positionHint": "right" },
    { "id": "background2", "type": "background-audio", "index": 9, "positionHint": "right" },
    { "id": "background3", "type": "background-audio", "index": 10, "positionHint": "right" },
    { "id": "background4", "type": "background-audio", "index": 11, "positionHint": "right" },
    { "id": "background5", "type": "background-audio", "index": 12, "positionHint": "right" },
    { "id": "background6", "type": "background-audio", "index": 13, "positionHint": "right" },
    { "id": "background7", "type": "background-audio", "index": 14, "positionHint": "right" },
    { "id": "background8", "type": "background-audio", "index": 15, "positionHint": "right" }
  ],
  "timing": [
    { "tick": 0, "type": "bpm", "value": 150.0 },
    { "tick": 3840, "type": "bar" },
    { "tick": 7680, "type": "stop", "durationTicks": 480 }
  ],
  "notes": [
    {
      "tick": 0,
      "lane": "key1",
      "type": "tap",
      "audioId": "kick_001"
    },
    {
      "tick": 960,
      "lane": "key2",
      "type": "long",
      "durationTicks": 1920,
      "audioId": "synth_004"
    }
  ],
  "backgroundAudio": [
    {
      "tick": 0,
      "audioId": "bgm_intro"
    }
  ]
}
```

### 5.2 音数制限の解決

既存BMSのように2文字IDへ音を割り当てる方式をやめる。

NBMSでは `audioId` を任意長の文字列IDまたは数値IDとする。

例:

- `kick_001`
- `snare_layer_soft_03`
- `lead_phrase_a_120bpm`
- `audio:000001`

実装上は音ファイル内のインデックステーブルで `audioId` から音声データへ解決する。

これにより、理論上の音数上限はフォーマットではなく実装上限に移る。仕様としては最低65535音以上を扱えることを推奨する。

## 6. 音ファイル仕様案

拡張子: `.nbma`

役割:

- 多数の音声素材を1ファイルにまとめる
- 音声素材のインデックスを持つ
- 圧縮音声または可逆圧縮音声を格納する
- 必要に応じて暗号化する
- 音源開発者の権利情報を保持する

### 6.1 コンテナ構造案

```text
NBMA Header
Index Table
Metadata Block
Audio Data Blocks
Integrity Block
```

### 6.2 ヘッダー

保持する情報:

- magic: `NBMA`
- formatVersion
- indexOffset
- metadataOffset
- dataOffset
- flags
- encryptionType
- hashAlgorithm

### 6.3 インデックステーブル

各音声素材ごとに以下を持つ。

- audioId
- codec
- sampleRate
- channels
- durationMs
- loopStartMs
- loopEndMs
- offset
- length
- originalHash
- encrypted
- rightsId

### 6.4 音声コーデック

候補:

- FLAC: 可逆。音質重視、サイズ削減、ゲーム用途で扱いやすい。NBMSでは必須対応とする
- Opus: 非可逆。サイズ削減に強い。短い効果音にも使えるが、厳密な波形再現には注意
- PCM: デバッグ用または互換用

推奨:

- 配布用の標準はFLAC
- Opusは任意対応の追加コーデックとする
- 制作中はPCM/FLAC
- プレイヤーは最低FLAC対応を必須にする

### 6.5 ディスクI/O削減

多数のWAVファイルを個別に開く方式をやめ、単一音源パックから読み込む。

期待効果:

- ファイルオープン回数の削減
- ランダムアクセスの局所化
- 事前読み込み、ストリーミング、キャッシュ制御が容易
- 譜面開始前のロード処理を最適化しやすい

プレイヤーは以下の実装を推奨する。

- 譜面開始前に必要音声のインデックスだけを読む
- 短い効果音はメモリへ事前展開
- 長いBGMやBGA同期音声はストリーミング
- 同じ音声が複数ノーツから参照される場合は共有バッファを使う

## 7. 暗号化・署名・編集制御

### 7.1 前提

暗号化だけで「絶対に取り出せない音源」を実現することは難しい。プレイヤーが再生できる以上、最終的には復号済みの音がメモリや音声出力に現れる。

そのためNBMSでは、以下を組み合わせて現実的な保護を行う。

- 音ファイルの暗号化
- 作者署名による改ざん検出
- 編集権限を持つ秘密鍵による再署名
- プレイヤー側の署名検証
- 権利情報の明示
- 配布パッケージ単位のハッシュ検証

### 7.2 暗号化対象

暗号化対象の候補:

- 音ファイル全体
- 音声データブロックのみ
- 譜面ファイル
- ヘッダー内の一部メタデータ

推奨:

- 音声データブロックは暗号化可能
- 譜面ファイルは原則署名対象とし、必要に応じて暗号化可能
- ヘッダーは曲情報検索のため原則平文。ただし署名対象にする

### 7.3 鍵管理

作者は公開鍵・秘密鍵のペアを持つ。

- 秘密鍵: 作者が保持。譜面や音源パックを編集・再署名するために使用
- 公開鍵: ヘッダーまたは外部の作者証明書として配布。プレイヤーが検証に使用

パッケージには以下を含める。

- 作者公開鍵ID
- 署名
- 対象ファイルのハッシュ
- 必要に応じて暗号化されたコンテンツ鍵

### 7.4 編集不可の考え方

「作成者以外に編集不可」は、実装上は以下の意味に定義する。

- 第三者がファイルを書き換えること自体はOS上では防げない
- ただし、書き換えたファイルは正規作者の署名検証に失敗する
- プレイヤーは署名失敗時に警告または再生拒否できる
- 正規の編集ツールは、秘密鍵を持たないユーザーに再署名を許可しない

つまり、物理的な編集防止ではなく、正規性の証明と改ざん検出を仕様化する。

### 7.5 推奨アルゴリズム

- 署名: Ed25519
- ハッシュ: SHA-256
- 共通鍵暗号: AES-256-GCM または ChaCha20-Poly1305
- 鍵導出: Argon2id または PBKDF2

MVPでは以下を推奨する。

- 署名: Ed25519
- ハッシュ: SHA-256
- 音声ブロック暗号化: AES-256-GCM

## 8. 権利保護メタデータ

音源開発者や作曲者の権利情報を機械可読にする。

ヘッダーと音ファイル双方に権利情報を持たせる。

例:

```json
{
  "rights": [
    {
      "id": "sound-dev-001",
      "name": "Sound Developer",
      "role": "sound-source",
      "license": "Commercial use prohibited without permission",
      "url": "https://example.com/license"
    }
  ]
}
```

音声素材ごとに `rightsId` を参照することで、どの音がどの権利条件に属するかを追跡できる。

## 9. 譜面ハッシュ

ランキング、スコア送信、譜面同一性判定のため、譜面ファイル `.nbmc` は譜面単位で個別のハッシュ値を持つ。

ハッシュ計算は環境依存しないことを必須とする。

推奨ルール:

- ハッシュ対象は譜面ファイルの論理内容とする
- 改行コード、空白、JSONオブジェクトのキー順に依存しない
- JSONを仕様で定めた正規化形式に変換してからSHA-256を計算する
- ヘッダー内の `charts[].hash` に譜面ごとのハッシュを記録する
- 署名対象には譜面ハッシュも含める

正規化JSONの詳細ルールは、`NBMS 0.1` のスキーマ策定時に確定する。

## 10. プレイヤー読み込み手順

プレイヤーは以下の順で読み込む。

1. ヘッダーファイルを読む
2. フォーマットバージョンを確認する
3. 署名対象ファイルのハッシュを検証する
4. 署名を検証する
5. 選択された譜面ファイルを読む
6. 譜面内で参照される `audioId` を列挙する
7. 音ファイルのインデックスを読む
8. 必要な音声だけをロードまたはストリーミング対象にする
9. 暗号化されている場合は復号鍵を解決する
10. 再生を開始する

署名検証に失敗した場合の推奨挙動:

- デフォルトでは警告を出す
- 公式・ランキング・大会モードでは再生拒否できる
- ローカル編集モードでは検証失敗を明示した上で読み込み可能にする

## 11. エディタ要件

NBMS対応エディタは以下を持つべき。

- ヘッダー編集
- 譜面編集
- 音声素材のインポート
- 音源パック生成
- 既存BMSからのインポート
- 署名生成
- 暗号化有無の選択
- 権利情報の入力
- 署名検証
- 未使用音声の検出
- 参照切れ `audioId` の検出

作者が秘密鍵を持っている場合のみ、正規署名付きパッケージを書き出せる。

### 11.1 差分譜面・譜面追加

作者による暗号化がなされていない場合、譜面エディタは既存パッケージに譜面を追加できる。

譜面追加時の基本動作:

- 新しい `.nbmc` 譜面ファイルを追加する
- ヘッダーファイル `.nbmh` の `charts` 配列に譜面情報を追記する
- 追加譜面のハッシュを計算し、`charts[].hash` に記録する
- 元の音源パック `.nbma` は原則変更しない
- 署名済みパッケージの場合、作者秘密鍵なしで再署名はできない

暗号化または署名ポリシーにより編集不可とされている場合、エディタは譜面追加を拒否するか、非正規のローカル改変として明示する。

## 12. 既存BMSからの変換方針

変換ツールは以下を行う。

- `#TITLE`, `#ARTIST`, `#GENRE`, `#BPM` などをヘッダーへ移す
- `#WAVxx` を `audioId` に変換する
- WAVファイルを音ファイル `.nbma` に格納する
- チャンネルデータを譜面イベントへ変換する
- 小節長変更、BPM変更、STOPをNBMSタイミングイベントへ変換する
- BGA関連は `.nbmg` メディアパックとして扱う

変換時の `audioId` 例:

```text
#WAV01 kick.wav   -> wav_01_kick
#WAVAB snare.wav  -> wav_ab_snare
```

## 13. 決定事項

現時点で以下の方針を採用する。

- ヘッダー形式はJSONに固定する
- 譜面形式はJSONに固定する。バイナリ形式は後で検討する
- 音源パックのコンテナは、実装しやすい既存コンテナがあれば利用する
- 必須音声コーデックはFLACとする
- BGA、動画、画像素材は音源パックとは別の `.nbmg` メディアパックにする
- ランキングやスコア送信用に、譜面単位で別々のハッシュ値を持つ
- 譜面ハッシュは環境依存しない正規化ルールで計算する
- 作者による暗号化がなされていない場合は、譜面エディタで譜面追加を可能にする
- 譜面追加時は `.nbmc` が増え、ヘッダーファイル `.nbmh` の `charts` を書き換える

引き続き検討する項目:

- 暗号化コンテンツの鍵配布方式
- 作者公開鍵の信頼モデル

## 14. 次に作るべきもの

### 14.1 仕様MVP

まずは暗号化なしで、以下を実装できる最小仕様を固める。

- `.nbmh` ヘッダーJSONスキーマ
- `.nbmc` 譜面JSONスキーマ
- `.nbma` 音源パックのバイナリ構造
- `audioId` 解決ルール
- タイミング計算ルール
- ハッシュ計算ルール

成果物:

- `spec/header.schema.json`
- `spec/chart.schema.json`
- `spec/audio_container.md`
- `examples/minimal/`

### 14.2 変換ツール

既存BMSをNBMSへ変換するCLIツールを作る。

機能:

- BMSパーサ
- WAV参照解析
- 音声パック生成
- ヘッダー生成
- 譜面生成
- 参照切れチェック

成果物:

```text
tools/bms2nbms/
```

### 14.3 検証用プレイヤー

本格的な音ゲープレイヤーではなく、仕様検証用の最小プレイヤーを作る。

機能:

- ヘッダー読み込み
- 譜面読み込み
- 音源パック読み込み
- `audioId` 解決
- タイミングに合わせた音声再生
- ログ出力

成果物:

```text
tools/nbms_probe_player/
```

### 14.4 署名・暗号化の追加

MVPの読み書きが動いてから、署名と暗号化を追加する。

実装順:

1. SHA-256によるファイルハッシュ
2. Ed25519によるヘッダー署名
3. 署名検証CLI
4. 音声ブロック単位のAES-256-GCM暗号化
5. エディタ用の鍵管理

成果物:

```text
tools/nbms_sign/
tools/nbms_verify/
```

### 14.5 エディタ仕様

最後に、譜面制作フローを定義する。

必要な画面:

- 曲情報
- 譜面編集
- 音声素材管理
- 権利情報管理
- 書き出し
- 署名・暗号化

この段階で、既存BMSエディタとの連携やプラグイン方式も検討する。

## 15. 推奨ロードマップ

### Phase 1: 仕様ドラフト

- ヘッダーJSON案を確定
- 譜面JSON案を確定
- 音源パック構造案を確定
- 最小サンプルを手書きで作成

Phase 1成果物:

- `spec/header.schema.json`
- `spec/chart.schema.json`
- `spec/audio_container.md`
- `spec/hash_canonicalization.md`
- `examples/minimal/`

### Phase 2: コンバータ試作

- 既存BMSを読み込む
- `.nbmh` と `.nbmc` を出力する
- WAVを単純連結した `.nbma` を出力する
- 暗号化はまだ入れない

Phase 2成果物:

- `tools/bms2nbms/`
- `examples/bms_phase2/`
- `examples/converted_phase2/`

現時点のコンバータは、既存BMSで参照されている音声ファイルを `.nbma` ZIPコンテナへそのまま格納する。NBMS 0.1の必須コーデックはFLACだが、Phase 2ではトランスコード機能を実装せず、WAVなどの非FLAC音源は警告を出して格納する。FLAC変換はPhase 2後続またはPhase 3前の改善項目とする。

### Phase 3: プレイヤー試作

- `.nbmh`, `.nbmc`, `.nbma` を読み込む
- 音声再生タイミングを検証する
- 大量音声時のロード速度を測定する

Phase 3成果物:

- `tools/nbms_probe_player/`

現時点の検証用プレイヤーは、実音声を出力するゲームプレイヤーではなく、NBMSパッケージを読み込んで以下を検証するprobe playerとする。

- ヘッダー、譜面、音源パックを読み込む
- ヘッダー内の音源パックハッシュを検証する
- 譜面単位の正規化JSONハッシュを検証する
- `.nbma` ZIP内の `manifest.json` と音声ファイルを検証する
- 譜面が参照する `audioId` が音源パックに存在するか確認する
- BPMとSTOPを考慮してtickを再生時刻へ変換する
- イベントスケジュールをログとして出力する
- `--realtime` により、実時間に沿ってイベントログを出力する
- `.nbma` 内のFLACをデコードし、譜面イベントに沿ってWAVへミックスする
- `--play` により、Windows標準のWAV再生機能でレンダリング結果を再生する

WAVからFLACへの変換は、Phase 3時点ではプロトタイプ内蔵のPCM16 WAV to FLAC変換を使う。生成されるFLACはverbatim subframeを使う単純なFLACであり、NBMSコンテナと再生経路の検証を目的とする。正式な制作ツールやプレイヤーでは、成熟したFLACエンコーダ/デコーダへ置き換える。

### Phase 4: 保護機能

- 署名を追加
- 改ざん検出を追加
- 音声パック暗号化を追加

Phase 4成果物:

- `tools/nbms_keygen/`
- `tools/nbms_sign/`
- `tools/nbms_verify/`
- `tools/nbms_encrypt_audio/`
- `examples/converted_phase4/`
- `examples/phase4_keys/`

Phase 4では、作者鍵によるヘッダー署名、ヘッダー・譜面・音源パックのハッシュ検証、音源パック内の音声ペイロード暗号化を実装する。

署名:

- 作者鍵はEd25519とする
- 秘密鍵は作者が保持する
- 公開鍵はヘッダーに埋め込むか、外部公開鍵ファイルとして配布する
- ヘッダー署名時は `security.signature` を除いた正規化JSONを署名対象にする
- 署名対象には、ヘッダー内の譜面ハッシュと音源パックハッシュが含まれる

音源暗号化:

- `.nbma` は引き続きZIPコンテナとする
- `manifest.json` は検索・検証のため平文で保持する
- 個々の音声ペイロードのみ暗号化する
- 暗号化された音声ファイルは `.enc` として格納する
- manifestにはnonce、認証タグ、元ファイルハッシュ、暗号化後ファイルハッシュを記録する
- Phase 4プロトタイプでは `ChaCha20-Poly1305` と `PBKDF2-HMAC-SHA256` を使う

注意:

現時点の暗号実装は、外部依存なしで仕様検証するためのプロトタイプである。正式実装では、監査済みの暗号ライブラリを利用し、パスフレーズ共有ではなく公開鍵で包んだコンテンツ鍵やOSの鍵ストアを使う方式を検討する。

### Phase 5: 制作環境

- エディタまたは既存エディタ連携を作る
- 作者鍵管理を作る
- 配布パッケージ作成機能を作る

Phase 5成果物:

- `tools/nbms_editor/`
- `tools/nbms_pack/`
- `spec/package_container.md`
- `examples/converted_phase5_from_bms/`

Phase 5では、GUIとして最小構成のNBMSエディタを新規実装する。

エディタの範囲:

- `.nbmh` ヘッダーJSONを読み込む
- ヘッダーが参照する `.nbmc` 譜面JSONを読み込む
- ヘッダーJSONと譜面JSONを編集して保存する
- 譜面ノーツを表形式で編集する
- `.nbma` の `manifest.json` から音源一覧を表示する
- 譜面が参照する `audioId` と音源一覧を照合し、参照切れをGUI上に表示する
- 保存時に譜面ハッシュと音源パックハッシュを再計算する
- 既存署名がある状態で鍵なし保存した場合は、署名を外して非署名状態に戻す
- 作者秘密鍵を読み込んだ場合は、保存時に署名できる
- 作者鍵を新規生成できる
- 通常形式のNBMSとして保存できる
- 任意で暗号化コピーとして保存できる
- 既存BMSから最小NBMSへ変換できる
- `.nbmp` 配布パッケージを作成できる

配布パッケージ:

- `.nbmp` はZIPコンテナとする
- `package_manifest.json` を含める
- `song.nbmh`、参照譜面、`.nbma`、必要に応じて `.nbmg` を含める
- 改ざん検出と署名検証は、パッケージではなく `song.nbmh` 内のハッシュと署名を基準にする

現時点のエディタはピアノロール型の視覚的なノーツ配置編集ではなく、NBMSファイルの読み書き、JSON編集、ノーツ表編集、音源参照確認、保存、署名、暗号化、変換、配布作成を確認するための制作環境プロトタイプである。

## 16. 最初の具体的タスク

最初に作るべきものは、実装可能性を確認するための小さな仕様セットである。

優先順位:

1. `NBMS 0.1` としてヘッダーと譜面のJSONスキーマを作る
2. 音源パック `.nbma` の最小バイナリ仕様を作る
3. 手書きの最小サンプル曲を作る
4. 既存BMSから最小サンプルへ変換するCLIを作る
5. 署名・暗号化なしで再生できる検証プレイヤーを作る
6. その後、署名と暗号化を追加する

この順番にすると、暗号化や権利保護の設計に入る前に、そもそも新しいデータ構造でBMS的な演奏が成立するかを確認できる。
