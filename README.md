# RoomRelay

Stream Windows 11 system audio to Sonos speakers over the LAN — whole
system or a single application.

A modern .NET 9 / WinUI 3 replacement for
[SWYH (Stream What You Hear)](https://www.streamwhathyouhear.com/), which
has been unmaintained since ~2019 and still targets old .NET Framework.

**No third-party audio dependencies.** AAC encoding, sample-rate
conversion, and WASAPI capture all go through built-in Windows APIs
(Media Foundation + raw WASAPI via CsWin32). No FFmpeg, no NAudio.

## Requirements

| | Minimum | Recommended |
|---|---|---|
| **OS** | Windows 10 22H2 | Windows 11 23H2 or later |

> **Zero dependencies.** The release build is fully self-contained — .NET 9
> runtime, Windows App Runtime 1.8, and all libraries are bundled. Nothing
> to install beforehand.

### Why Windows 11 is recommended

- **Process loopback** (`ActivateAudioInterfaceAsync` for per-app capture)
  requires Windows 10 21H2 or later. On Windows 10 this feature may be
  unavailable or unstable depending on patch level.
- **WinUI 3** performance and compatibility are best on Windows 11.
- **WASAPI shared-mode loopback** works on both, but the developer
  test matrix is Windows 11-only.

## Download

[![GitHub Release](https://img.shields.io/github/v/release/guicn555/RoomRelay)](https://github.com/guicn555/RoomRelay/releases)

Download the latest ZIP from the [Releases page](https://github.com/guicn555/RoomRelay/releases).

Extract and run `RoomRelay.exe`. **No additional runtime or framework
installation required** — the build is fully self-contained (all dependencies
are bundled). Settings are stored in `%APPDATA%\RoomRelay`.

> Prefer an installer? See [`csharp/installer/`](csharp/installer/) for an
> [Inno Setup](https://jrsoftware.org/isinfo.php) script that builds a
> standard Windows `.exe` installer with shortcuts and clean uninstall.

## What it does

- **Captures** the default Windows render endpoint via WASAPI loopback
  *or* a specific application's audio output via process loopback
  (`ActivateAudioInterfaceAsync` on the `VAD\Process_Loopback` virtual
  device).
- **Resamples / converts** to 48 kHz 16-bit stereo (pure-C# pass-through
  when the device already mixes at 48 kHz; otherwise the Windows Media
  Foundation resampler MFT, `CLSID_CResamplerMediaObject`).
- **Encodes** to AAC-LC @ 256 kbps using the Windows Media Foundation
  AAC encoder MFT (`CLSID_CMSAACEncMFT`) configured with
  `MF_MT_AAC_PAYLOAD_TYPE = 1` so the output is already ADTS-framed.
- **Serves** the ADTS stream on `http://<host>:8000/stream.aac` from a
  raw `TcpListener` HTTP/1.0 server (no chunked encoding — Sonos
  rejects it).
- **Discovers** Sonos speakers via SSDP M-SEARCH on every usable network
  interface — IPv4 (`239.255.255.250`) and IPv6 (`ff02::c`) — with
  concurrent per-socket receive loops. Resolves user-set zone names from
  `ZoneGroupTopology` so the UI shows "Living Room" instead of `RINCON_…`.
- **Collapses stereo pairs** into a single zone by querying
  `ZoneGroupTopology` and keeping only coordinators.
- **Pushes** the stream URL to the chosen speaker via UPnP SOAP
  (`SetAVTransportURI` with the `x-rincon-mp3radio://` prefix, then
  `Play`).
- **Mutes** the default render endpoint while streaming so the room
  doesn't hear PC audio twice (loopback captures pre-mute, so Sonos
  still gets data).
- **Injects silence** when loopback is idle so Sonos doesn't disconnect.

## UI

WinUI 3 window with Mica backdrop and declarative XAML UI:

- Speaker list with Rescan and auto-select on launch (last speaker
  remembered by UDN).
- Source picker: **Whole system** or **Per application** (combo
  populates from active audio sessions, refreshed every 2 s).
- Start / Stop with `ProgressRing` busy state and mutual disable.
- Volume (0–8× with soft clip), per-channel L/R gain with Link, 3-band
  EQ, per-channel delay — all in collapsible `Expander` sections.
- Live VU meter and spectrum analyzer (Win2D, 30 fps).
- `InfoBar` error notification when the pipeline crashes or the audio
  endpoint format changes.
- Tray icon with Show / Quit; close-to-tray on window X.
- All slider positions and the last speaker persist to
  `%APPDATA%\RoomRelay\settings.json` with debounced disk writes
  (no more hundreds of file writes per slider drag).

## Security and privacy notes

- This is an unofficial Sonos utility and is not affiliated with or endorsed
  by Sonos.
- While streaming, the app runs a local HTTP server so the selected Sonos
  speaker can fetch the audio. The server binds to the local network interface
  used to reach that speaker and uses a random per-run stream path.
- The stream is not encrypted or authenticated. Only allow the Windows
  Defender Firewall prompt on trusted private networks.
- Settings are stored locally under `%APPDATA%\RoomRelay`. They may
  include the last selected speaker UDN, but no audio is recorded to disk by
  the app.

## Building from source

### Prerequisites

- **.NET 9 SDK** (9.0.313 or later).
- **Windows App Runtime 1.8** framework package (pre-installed on most
  Windows 11; run `Get-AppxPackage *WindowsAppRuntime.1.8*` to check).
- That's it — no extra DLLs to drop in.

### Build & run

```powershell
cd csharp
dotnet build -c Release
dotnet run --project src/SonosStreaming.App
```

First run will trigger a Windows Defender Firewall prompt — allow on
the network profile where your Sonos lives so it can fetch the stream.

## Architecture

```
csharp/
  SonosStreaming.sln
  Directory.Build.props        # net9.0-windows, x64, unsafe, C# preview
  src/
    SonosStreaming.Core/        # audio, network, DSP, pipeline
      Audio/                    # WASAPI capture, MF encoder, resampler, DSP
      Network/                  # SSDP, SOAP, HTTP stream server
      Pipeline/                 # PipelineRunner orchestration
      State/                    # AppCore state machine, AppSettings
    SonosStreaming.App/         # WinUI 3 unpackaged desktop app
      Views/                    # MainPage.xaml (declarative, x:Bind)
      ViewModels/               # MainViewModel (CommunityToolkit.Mvvm)
      Controls/                 # VuMeterControl, SpectrumControl
      Converters/               # BoolToVisibilityConverter
      Tray/                     # H.NotifyIcon + native PopupMenu
  tests/
    SonosStreaming.Tests/       # xUnit + FluentAssertions + FsCheck
```

### Data flow

```
WASAPI / process-loopback capture → PcmFrameF32
                                        ↓
                  DSP (gain → EQ → delay → volume → VU → spectrum)
                                        ↓
                            Resampler (f32 → i16 @ 48 kHz)
                                        ↓
                           MfAacEncoder (Media Foundation MFT)
                                         ↓
                           ADTS-framed AAC accumulated in 16 KB batches
                                         ↓
                        BroadcastChannel<ReadOnlyMemory<byte>>
                                        ↓
                          StreamServer (TCP, HTTP/1.0)
                         /             |             \
                   Sonos conn     Sonos conn       ...
```

## Testing

```powershell
cd csharp
dotnet test
```

Unit tests use xUnit + FluentAssertions + FsCheck. The e2e test
(`MockSonosE2E.cs`) spins up a mock Sonos HTTP + SOAP server on
`127.0.0.1:0`.

## Status

End-to-end working: whole-system capture, per-application capture,
real-time DSP, AAC streaming to a Sonos speaker or stereo pair,
endpoint-mute-while-streaming, settings persistence with debounced
saves, tray icon, native context menu, clean shutdown, pipeline crash
recovery with user-visible errors, audio endpoint format-change
detection, and dual-stack IPv4/IPv6 SSDP discovery. Audio backend is
FFmpeg-free and NAudio-free — only Windows-native APIs. Builds with
**zero warnings**.

## License

MIT OR Apache-2.0.
