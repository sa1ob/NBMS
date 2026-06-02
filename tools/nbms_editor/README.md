# nbms_editor

Minimal NBMS editor for Phase 5.

This is a small Tkinter application focused on NBMS read/write/save workflows rather than visual note editing.

## Start

```text
python tools/nbms_editor/nbms_editor.py
```

## Features

- Open an existing `song.nbmh`.
- Edit header JSON.
- Edit referenced chart JSON files.
- Edit chart notes in a table view.
- View audio archive entries from `.nbma`.
- Check missing `audioId` references from charts.
- Save NBMS in normal form.
- Load or generate an author key.
- Optionally sign saved headers.
- Save an encrypted copy using a passphrase.
- Convert a BMS file into a minimal NBMS folder.
- Create a distributable `.nbmp` package.

The editor recalculates chart hashes and the audio archive hash on save. If no private key is selected, saving clears any existing signature because the file contents may have changed.

## Note Table

Each chart gets a `Notes:` tab with these editable fields:

- `tick`
- `lane`
- `type`
- `audioId`
- `durationTicks`

Use `Apply to JSON` to write the table contents back into the chart JSON tab. Normal save also applies note tables before writing files.

## Audio List and Reference Check

The `Audio` tab reads `manifest.json` from the referenced `.nbma` archive and lists:

- `audioId`
- codec
- duration
- sample rate
- channel count
- encrypted state
- archive path

The `Reference Check` tab compares chart note/background-audio `audioId` references against the audio manifest. Missing references are shown by chart, reference type, index, and `audioId`.

---

# nbms_editor 日本語版

Phase 5用の最小NBMS editorです。

これはvisual note editingよりも、NBMSの読み込み、書き込み、保存workflowに焦点を置いた小さなTkinter applicationです。

## 起動

```text
python tools/nbms_editor/nbms_editor.py
```

## 機能

- 既存の `song.nbmh` を開く
- header JSONを編集
- 参照されているchart JSON fileを編集
- chart notesをtable viewで編集
- `.nbma` からaudio archive entriesを表示
- chartから参照される `audioId` の欠落を確認
- 通常形式でNBMSを保存
- 作者鍵の読み込みまたは生成
- 保存したheaderへの任意署名
- passphraseを使った暗号化copy保存
- BMS fileから最小NBMS folderへ変換
- 配布用 `.nbmp` package作成

保存時にはchart hashとaudio archive hashを再計算します。private keyが選択されていない場合、file contentsが変わった可能性があるため既存signatureをclearします。

## Note Table

各chartには `Notes:` tabがあり、以下のfieldを編集できます。

- `tick`
- `lane`
- `type`
- `audioId`
- `durationTicks`

`Apply to JSON` を使うと、table内容をchart JSON tabへ反映します。通常保存時にも、file書き込み前にnote tableが適用されます。

## Audio List and Reference Check

`Audio` tabは、参照されている `.nbma` archiveから `manifest.json` を読み、以下を表示します。

- `audioId`
- codec
- duration
- sample rate
- channel count
- encrypted state
- archive path

`Reference Check` tabは、chartのnote/background-audio `audioId` 参照をaudio manifestと比較します。欠落した参照はchart、reference type、index、`audioId` として表示されます。
