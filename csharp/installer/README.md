# Creating the Installer

This folder contains an [Inno Setup](https://jrsoftware.org/isinfo.php) script to build a Windows installer for RoomRelay.

## Prerequisites

1. Download and install **Inno Setup 6.2 or later**: https://jrsoftware.org/isdl.php
2. Publish the app variants:
   ```powershell
   cd ..\..\csharp
   dotnet publish src\SonosStreaming.App -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o publish\RoomRelay-v1.0.8-win-x64-full
   dotnet publish src\SonosStreaming.App -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -o publish\RoomRelay-v1.0.8-win-x64-light
   ```

## Build the Installer

Open `installer.iss` in Inno Setup Compiler (or right-click → **Compile**), or run from command line:

```powershell
iscc installer.iss /DPublishDir=RoomRelay-v1.0.8-win-x64-full /DArtifactSuffix=win-x64-full
iscc installer.iss /DPublishDir=RoomRelay-v1.0.8-win-x64-light /DArtifactSuffix=win-x64-light
```

The installers `RoomRelay-Setup-1.0.8-win-x64-full.exe` and
`RoomRelay-Setup-1.0.8-win-x64-light.exe` will be created in `csharp\`.

## What the Installer Does

- Installs to `%ProgramFiles%\RoomRelay` (or `%LocalAppData%\Programs\RoomRelay` for non-admin)
- Creates Start Menu shortcut
- Optional Desktop shortcut
- Handles uninstall via Windows Settings → Apps
- No registry clutter — standard Inno Setup uninstall

## Silent Install (Enterprise/Deployment)

```powershell
RoomRelay-Setup-1.0.8-win-x64-full.exe /VERYSILENT /NORESTART
```

## Size

| Artifact | Approx. Size |
|---|---|
| Publish folder | varies |
| ZIP | varies |
| Installer (LZMA2 compressed) | varies |
