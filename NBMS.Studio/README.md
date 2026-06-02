# NBMS Studio 日本語版

NBMS Studioは、NBMSドラフトフォーマットを検証するための実験的なEditor/Viewerです。

使用技術:

- C#
- .NET 8
- Avalonia

## ドキュメント

- [NBMS Studio仕様](docs/NBMS_STUDIO_SPEC.md)
- [Build手順](docs/BUILD.md)
- [未実装・未検討事項](docs/UNIMPLEMENTED.md)

## 簡易Build

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

.NET 8 SDKが必要です。単一exeとしてpublishする手順はBuild手順を参照してください。

---

# NBMS Studio

NBMS Studio is an experimental Editor/Viewer for the NBMS draft format.

It is built with:

- C#
- .NET 8
- Avalonia

## Documents

- [NBMS Studio specification](docs/NBMS_STUDIO_SPEC.md)
- [Build guide](docs/BUILD.md)
- [Unimplemented / unresolved topics](docs/UNIMPLEMENTED.md)

## Quick Build

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

The .NET 8 SDK is required. See the build guide for single-executable publishing.



