# NBMS Studio 日本語版

NBMS Studioは、NBMSドラフトフォーマットを検証するための実験的なEditor/Viewerです。

使用技術:

- C#
- .NET 8
- Avalonia
- MonoGame（Viewer描画ループ試作用）

## ドキュメント

- [NBMS Studio仕様](docs/NBMS_STUDIO_SPEC.md)
- [Build手順](docs/BUILD.md)
- [未実装・未検討事項](docs/UNIMPLEMENTED.md)

## 簡易Build

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

.NET 8 SDKが必要です。単一exeとしてpublishする手順はBuild手順を参照してください。

## MonoGame Viewer試作

描画同期とFPS検証用に、別プロセスのMonoGame Viewerを追加しています。

```powershell
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj -- path\to\song.nbmh --chart chart-id
```

NBMS Studio本体からは、`外部ビューワ > MonoGame Viewerを開く` またはツールバーの `MonoGame` から起動できます。現時点では描画ループ検証用で、音声再生は未接続です。

StudioからMonoGame Viewerを起動する場合は、先にMonoGame Viewer projectをbuildしてください。build済みexeが見つかった場合は、Studioは`dotnet run`ではなくViewer exeを直接起動します。

---

# NBMS Studio

NBMS Studio is an experimental Editor/Viewer for the NBMS draft format.

It is built with:

- C#
- .NET 8
- Avalonia
- MonoGame (prototype viewer render loop)

## Documents

- [NBMS Studio specification](docs/NBMS_STUDIO_SPEC.md)
- [Build guide](docs/BUILD.md)
- [Unimplemented / unresolved topics](docs/UNIMPLEMENTED.md)

## Quick Build

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

The .NET 8 SDK is required. See the build guide for single-executable publishing.

## MonoGame Viewer Prototype

NBMS Studio includes a separate MonoGame Viewer prototype for render-loop and FPS validation.

```powershell
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj -- path\to\song.nbmh --chart chart-id
```

From NBMS Studio, use `External Viewer > Open MonoGame Viewer` or the `MonoGame` toolbar button. Audio playback is not connected yet.

When launching MonoGame Viewer from Studio, build the MonoGame Viewer project first. If the built executable is found, Studio launches the Viewer exe directly instead of using `dotnet run`.

