
# NBMS Studio Build手順 日本語版

## 必要環境

- Windows
- .NET 8 SDK
- 初回NuGet package restore用のインターネット接続

`dotnet` が使用可能か確認します。

```powershell
dotnet --info
```

`dotnet` にPATHが通っていない場合は、SDKのフルパスを指定します。

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' --info
```

## Restore

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet restore .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

## Build

```powershell
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

`dotnet` にPATHが通っていない場合:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
& 'C:\Program Files\dotnet\dotnet.exe' build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

## Run

```powershell
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

StudioからMonoGame Viewerを起動する場合は、事前にMonoGame Viewer projectをbuildしてください。build済みexeが存在する場合、Studioは`dotnet run`ではなくViewer exeを直接起動します。

## 単一exe publish

このプロジェクトはsingle-file publishを想定しています。

```powershell
dotnet publish .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

出力先:

```text
NBMS.Studio/src/NBMS.Studio.App/bin/Release/net8.0/win-x64/publish/
NBMS.Studio/src/NBMS.Studio.MonoGameViewer/bin/Release/net8.0-windows/win-x64/publish/
```

## 依存関係

主な依存関係:

- Avalonia
- Avalonia.Desktop
- Avalonia.Controls.DataGrid
- NAudio
- NAudio.Vorbis
- MonoGame.Framework.WindowsDX

初回Build前にNuGet restoreが必要です。

---

# NBMS Studio Build Guide

## Required Environment

- Windows
- .NET 8 SDK
- Internet access for first-time NuGet package restore

Check that `dotnet` is available:

```powershell
dotnet --info
```

If `dotnet` is not in `PATH`, use the full path to the SDK executable, for example:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' --info
```

## Restore

```powershell
dotnet restore .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet restore .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

## Build

```powershell
dotnet build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
dotnet build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

If `dotnet` is not in `PATH`:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
& 'C:\Program Files\dotnet\dotnet.exe' build .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj
```

## Run

```powershell
dotnet run --project .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj
```

When launching MonoGame Viewer from Studio, build the MonoGame Viewer project first. If the built executable exists, Studio launches the Viewer exe directly instead of using `dotnet run`.

## Publish Single Executable

The project is configured for single-file publishing.

```powershell
dotnet publish .\NBMS.Studio\src\NBMS.Studio.App\NBMS.Studio.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish .\NBMS.Studio\src\NBMS.Studio.MonoGameViewer\NBMS.Studio.MonoGameViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output will be generated under:

```text
NBMS.Studio/src/NBMS.Studio.App/bin/Release/net8.0/win-x64/publish/
NBMS.Studio/src/NBMS.Studio.MonoGameViewer/bin/Release/net8.0-windows/win-x64/publish/
```

## Dependencies

Main runtime dependencies:

- Avalonia
- Avalonia.Desktop
- Avalonia.Controls.DataGrid
- NAudio
- NAudio.Vorbis
- MonoGame.Framework.WindowsDX

NuGet restore is required before the first build.
