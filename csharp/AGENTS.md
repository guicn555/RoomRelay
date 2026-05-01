# AGENTS.md — C# / .NET 10 WinUI3 RoomRelay

Guidance for working in the C# implementation.

## What this is

Windows-only .NET 10 / WinUI 3 desktop app that:

- Captures system audio via WASAPI loopback (raw CsWin32; no NAudio).
- Resamples / converts to 48 kHz s16 stereo (pure C# pass-through when the
  device is already at 48 kHz, otherwise the Windows Media Foundation
  resampler MFT).
- Encodes to AAC-LC @ 256 kbps using the Windows Media Foundation AAC encoder
  MFT, configured with `MF_MT_AAC_PAYLOAD_TYPE = 1` so output is already
  ADTS-framed.
- Serves the ADTS stream on `http://<host>:8000/stream.aac` from a raw
  `TcpListener` HTTP/1.0 server.
- Discovers Sonos speakers via SSDP (concurrent per-NIC, IPv4 + IPv6), pulls
  user-set zone names from `ZoneGroupTopology`, filters to coordinators, and
  pushes the URL via UPnP SOAP `SetAVTransportURI` + `Play`.
- Mutes the local render endpoint while streaming so the room doesn't hear
  the PC audio twice; loopback captures pre-mute, so Sonos still gets data.

There is **no FFmpeg dependency** and **no NAudio dependency**. Everything
that touches audio goes through Windows Media Foundation or raw WASAPI via
CsWin32-generated bindings.

## Build & run

```powershell
dotnet build                                  # debug build (all 3 projects)
dotnet build -c Release                       # release build
dotnet run --project src/SonosStreaming.App   # launch the app
dotnet test                                   # run unit + integration tests
```

**Prerequisites:**
- .NET 10 SDK.
- Windows App Runtime 2.0 framework package (run
  `Get-AppxPackage *WindowsAppRuntime.2.0*` to check).

The project uses **framework-dependent** deployment (`WindowsAppSDKSelfContained=false`,
`WindowsPackageType=None`) with a **custom `Program.cs`** entry point (`DISABLE_XAML_GENERATED_MAIN`).
The Windows App Runtime is resolved via `Bootstrap.Initialize(0x00020000)` at
startup — this must happen before any WinUI types are touched.

The app binds `0.0.0.0:8000` and sends SSDP multicast on first scan — a firewall
prompt is expected on first run.

## Solution layout

```
csharp/
  SonosStreaming.sln
  Directory.Build.props                  # shared: net10.0-win, x64, unsafe, C# preview
  src/
    SonosStreaming.Core/                  # .NET library — audio, network, state, pipeline
      NativeMethods.txt                   # CsWin32 bindings list (Core)
      Audio/
        PcmFrameTypes.cs                 # PcmFrameF32, PcmFrameI16, PcmConvert
        IAudioSource.cs                  # IAudioSource interface, MixFormat record
        WasapiCaptureBase.cs             # Template-method base for WASAPI capture sources
        WasapiLoopbackSource.cs          # Raw CsWin32 WASAPI loopback + silence injection
        ProcessLoopbackSource.cs         # ActivateAudioInterfaceAsync VAD\Process_Loopback
        SyntheticSource.cs              # Sine / silence / noise test source
        Resampler.cs                    # Pass-through @ 48 kHz, MF resampler MFT otherwise
        MfAacEncoder.cs                 # Media Foundation AAC encoder MFT (CLSID_CMSAACEncMFT)
        AdtsFrameScanner.cs             # ADTS header parse + scan utility
        EndpointMuteGuard.cs            # IAudioEndpointVolume mute guard via CsWin32
        AudioEndpointMonitor.cs         # Polls default endpoint format; raises FormatChanged
        Dsp/
          GainStage.cs                  # Per-channel gain (SIMD)
          VolumeStage.cs                # Global volume scalar with soft clip
          VuMeter.cs                    # RMS + peak per channel
          BiquadEqualizer.cs            # 3-band RBJ peaking EQ
          ChannelDelay.cs              # Per-channel ring buffer delay
          SpectrumAnalyzer.cs           # MathNet FFT, 64-band log-scale, -90..0 dB
      Network/
        BroadcastChannel.cs             # Per-subscriber Channel<T> fan-out
        StreamServer.cs                 # Raw TcpListener HTTP/1.0 server
        SsdpDiscovery.cs                # SSDP M-SEARCH (concurrent per-socket, IPv4+IPv6)
        SonosController.cs              # SOAP SetAVTransportURI / Play / Stop
        TopologyResolver.cs             # ZoneGroupState → coordinators + zone names
        LocalIpResolver.cs              # UDP-connect trick for NIC selection (dual-stack)
        ISonosController.cs             # Interface for testability
        ISsdpDiscovery.cs               # Interface for testability
        ITopologyResolver.cs            # Interface for testability
        IStreamServer.cs                # Interface for testability
      State/
        AppCore.cs                      # Idle / Starting / Streaming / Stopping state machine
        AppSettings.cs                  # JSON settings persistence (%APPDATA%\RoomRelay\settings.json)
      Pipeline/
        PipelineRunner.cs               # Orchestrates capture → DSP → encode → stream → SOAP
        PipelineOptions.cs              # Config constants
    SonosStreaming.App/                  # WinUI 3 unpackaged desktop app
      NativeMethods.txt                  # CsWin32 bindings list (App)
      Program.cs                        # Custom entry: Bootstrap + Application.Start
      App.xaml / App.xaml.cs            # DI container, Serilog, tray setup
      MainWindow.xaml.cs                # Mica backdrop, HWND caching, ShowWindow P/Invoke
      Assets/sonos-streaming.ico
      Views/
        MainPage.xaml                   # Declarative UI: cards, sliders, ListView, transport
        MainPage.xaml.cs                # Code-behind: minimal, binds to MainViewModel
      ViewModels/
        MainViewModel.cs                # CommunityToolkit.Mvvm partial properties + RelayCommands
      Services/
        SharedGuiBridge.cs              # Thin polling service: pipeline → VM push (33 ms)
      Controls/
        VuMeterControl.xaml.cs          # Track + colored bar L/R (binds via DPs)
        SpectrumControl.xaml.cs         # Win2D CanvasControl, repaints @ 30 fps (binds via DPs)
      Converters/
        BoolToVisibilityConverter.cs    # x:Bind converter for button busy states
      Tray/
        TrayIconHost.cs                 # H.NotifyIcon TaskbarIcon + native Win32 PopupMenu
  tests/
    SonosStreaming.Tests/                # xUnit + FluentAssertions + FsCheck
```

## Data flow

```
WASAPI capture thread → PcmFrameF32 → DSP (gain → EQ → delay → volume → VU → spectrum)
                                                  ↓
                                             Resampler (f32 → i16 @ 48 kHz)
                                                  ↓
                                        MfAacEncoder (Windows Media Foundation)
                                                  ↓
                                    ADTS-framed AAC bytes accumulated in 16 KB batches
                                                  ↓
                                        BroadcastChannel<ReadOnlyMemory<byte>>
                                                  ↓
                                         StreamServer (TCP, HTTP/1.0)
                                        /          |          \
                                  Sonos conn   Sonos conn    ...
```

- Capture runs on a dedicated MTA thread driven off WASAPI's
  `SetEventHandle` event; it pumps `IAudioCaptureClient::GetBuffer` /
  `ReleaseBuffer` and writes `PcmFrameF32` into a `Channel`.
- A separate background thread injects silence on idle so the encoder
  keeps producing frames even when nothing is playing on the PC.
- `PipelineRunner.PumpLoopAsync` runs on `Task.Run` (thread pool MTA).
  The MF encoder is constructed **inside** the pump task because MF MFTs
  are apartment-bound to the thread that creates them; constructing on
  the UI (STA) thread and using on the pump (MTA) thread access-violates
  inside `ProcessInput`.
- `MfAacEncoder` accumulates ADTS frames into an internal 16 KB batch
  buffer. `FlushChunk()` returns the accumulated bytes, reducing `byte[]`
  allocations from ~47/sec to ~4–5/sec.
- `BroadcastChannel` fans one encoded stream out to every connected Sonos;
  lagging subscribers are dropped and must reconnect.
- A polling task reads `BroadcastChannel.SubscriberCount` every 500 ms and
  publishes it to `SharedGuiBridge`, which pushes values into
  `MainViewModel` observable properties for UI binding.

## Key decisions & gotchas

1. **WinUI 3 unpackaged bootstrap.** Call `Bootstrap.Initialize(0x00020000)`
   **before** any WinUI types are used, and create `new App()` **inside**
   the `Application.Start` callback (creating it before causes
   `RPC_E_WRONG_THREAD`). The generated `ApplicationConfiguration.Initialize()`
   is not used; `Program.cs` calls `WinRT.ComWrappersSupport.InitializeComWrappers()`
   + `new App()` explicitly.

2. **SxS / FailFast crash with self-contained mode.**
   `WindowsAppSDKSelfContained=true` bundles WinRT DLLs (including
   `CoreMessagingXP.dll`) locally. When `Bootstrap.Initialize` also
   initializes the system runtime, two copies load in the same process and
   trigger a native FailFast at `Application.Start`. Use
   `WindowsAppSDKSelfContained=false` and rely on the system-installed
   Windows App Runtime 2.0 framework package.

3. **MF AAC encoder needs payload type 1 for ADTS.** The MFT can emit raw
   AAC (no framing) or ADTS. Sonos requires ADTS over HTTP, so we set
   `MF_MT_AAC_PAYLOAD_TYPE = 1` on the output media type and skip any
   manual ADTS wrapping.

4. **MF AAC encoder timestamps are mandatory.** `IMFTransform::ProcessInput`
   returns `MF_E_NOTACCEPTING` (0xC00D36B5) without `SetSampleTime` +
   `SetSampleDuration` on every input sample. Units are 100-ns; for 48 kHz
   the duration of one 1024-sample AAC frame is `1024 * 10_000_000 / 48000`
   hns.

5. **AAC encoder needs a sample accumulator.** WASAPI buffers are typically
   smaller than the 1024-sample AAC frame (~10 ms vs 21.3 ms). Without
   accumulation each buffer either produces zero AAC frames (buffer < 1024
   samples → whole buffer discarded) or drops the tail, which sounds
   choppy. `MfAacEncoder` carries leftover samples across `Encode()` calls
   in a 1024-sample ring and emits a full frame whenever the ring fills.

6. **MF MFTs are apartment-bound.** Construct the encoder on the same
   thread that uses it. The pump task is MTA, so `_encoder = new
   MfAacEncoder()` runs at the top of `PumpLoopAsync`. Creating the
   encoder on the UI (STA) thread and calling it from the pump (MTA)
   thread access-violates the first `ProcessInput`.

7. **WASAPI float detection via `WaveFormatExtensible.SubFormat`.**
   Shared-mode loopback on most devices delivers IEEE float in a
   `WaveFormatExtensible` whose `wFormatTag` reports `WAVE_FORMAT_EXTENSIBLE`,
   not `WAVE_FORMAT_IEEE_FLOAT`. Reading only `wFormatTag` misclassifies
   float audio as int and reinterprets the bit patterns, making Sonos
   play near-silent garbage. `WasapiCaptureBase.DecodeWaveFormat`
   inspects `SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT` first.

8. **`NextFrameAsync` must propagate `OperationCanceledException`.** The
   pump wraps each `NextFrameAsync` in a 100 ms timeout CTS so it can
   inject silence when capture stalls. If the source swallows the
   `OperationCanceledException` and returns `null`, the pump interprets
   that as "source exhausted" and exits — the stream dies after a few
   iterations whenever capture races the silence-injection timer. Let
   cancellation throw up to the pump.

9. **`IAudioClient.GetService` returns the RCW directly.** CsWin32's
   generated overload signature is `GetService(Guid* riid, out object
   ppv)`, not `out void* ppv`. Cast the `out object` straight to the
   target interface (e.g. `(IAudioCaptureClient)captureObj`) instead of
   round-tripping through `Marshal.GetObjectForIUnknown((IntPtr)x)` —
   that cast is `InvalidCastException: object → IntPtr`.

10. **`iface as IAudioClient` after `GetActivateResult` is unreliable.**
    The RCW that comes out of `IActivateAudioInterfaceAsyncOperation::
    GetActivateResult` sometimes QIs to `E_NOINTERFACE` for the standard
    IAudioClient IID on the first vtable call. Bypass with explicit
    `Marshal.GetIUnknownForObject` + `Marshal.QueryInterface` to get a
    fresh interface pointer, then `Marshal.GetObjectForIUnknown` on
    that.

11. **SSDP receive must be per-socket and concurrent.** `UdpClient.ReceiveAsync`
    ignores `Socket.ReceiveTimeout`. A sequential
    `foreach socket { await ReceiveAsync(ct); }` blocks forever on the
    first socket that never sees an SSDP reply (virtual adapters,
    Hyper-V, VPN NICs). `SsdpDiscovery.ScanAsync` runs one receive loop
    per socket with a shared deadline CTS and `Task.WhenAll`.

12. **Sonos zone names are entity-encoded inside the SOAP envelope.**
    `GetZoneGroupState` returns the inner XML as a single text node with
    `&lt;ZoneGroupMember … ZoneName=&quot;Living Room&quot;&gt;`.
    `TopologyResolver.ExtractZoneNames` decodes the entity entities
    once before parsing, then maps `UUID → ZoneName` so the
    user-visible room name replaces the SKU-style `friendlyName` from
    the device-description XML.

13. **Chunked encoding breaks Sonos.** `StreamServer` forces HTTP/1.0
    responses (no `Transfer-Encoding: chunked`). Without this, hyper /
    Kestrel-style framing causes a 404 from Sonos.

14. **`x-rincon-mp3radio://` URI prefix.** Makes Sonos treat the URL as
    a radio stream and skip file-format probing. Without it Sonos 404s
    on AAC. **Do NOT use this prefix for LPCM/WAV** — Sonos returns
    UPnP error 714 (Illegal MIME-type). LPCM must use a plain `http://`
    URI so Sonos respects the `audio/wav` Content-Type header.

15. **Stereo pairs / zone groups.** Only the Coordinator accepts `Play`.
    `TopologyResolver` filters non-coordinators by querying
    `GetZoneGroupState` on any one device.

16. **Endpoint mute vs loopback.** WASAPI loopback captures post-mix,
    post-endpoint-volume but **not** post-mute — so `SetMute(true)`
    silences the room without affecting what Sonos hears.

17. **State machine mustn't tear down on double-clicks.**
    `AppCore.BeginStart` throws if already in `Streaming`.
    `MainViewModel.StartAsync` catches that and returns
    early without touching the pipeline.

18. **Tray context menu on unpackaged WinUI 3.**
    `H.NotifyIcon.TaskbarIcon.ContextFlyout` (XAML `MenuFlyout`) lacks
    a visual-tree parent for off-screen tray icons, so the flyout pops
    in the bottom-left of the screen and clicks vanish. Use the native
    `H.NotifyIcon.Core.PopupMenu` invoked from the
    `RightClickCommand` handler at the cursor position, and bypass the
    XAML flyout entirely.

19. **Quit must let the window actually close.** `MainWindow.OnClosed`
    intercepts the close with `args.Handled = true` to hide-to-tray.
    `Application.Current.Exit()` triggers a window close that this
    handler then cancels — the app stays alive. The handler now checks
    a `_exiting` flag (`MainWindow.MarkExiting()`) that the tray Quit
    handler sets before calling `Exit()`.

20. **Partial properties need `LangVersion=preview` in 8.4.0.** The MVVM
    Toolkit 8.4 source generator implements
    `[ObservableProperty] public partial T Prop { get; set; }` only when
    the C# language version is `preview`. With `LangVersion=13` the
    generator produces nothing and you get
    `CS9248: must have an implementation part`. `Directory.Build.props`
    sets `<LangVersion>preview</LangVersion>`.

21. **`.ConfigureAwait(false)` everywhere in Core.** Every `await` in
    `SonosStreaming.Core` uses `.ConfigureAwait(false)` so library code
    doesn't capture the caller's synchronization context. This prevents
    deadlocks when Core methods are called from UI-thread-bound code.

22. **Settings save is debounced.** `AppSettings.Save()` resets a 500 ms
    timer instead of writing immediately. Callers (slider change handlers)
    can invoke it freely without flooding the disk. The actual flush is
    protected by a `Lock` for thread safety.

23. **ADTS frame batching.** `MfAacEncoder` no longer returns a
    `List<ReadOnlyMemory<byte>>` per `Encode()` call. Instead it
    appends ADTS frames to an internal batch buffer and emits chunks
    via `FlushChunk()`. This amortizes allocations and reduces GC
    pressure by ~90% on the hot path.

24. **TCP tuning for real-time streaming.** `StreamServer` sets
    `TcpClient.NoDelay = true` and scales `SendBufferSize` by format:
    8 KB for AAC (~256 ms at 256 kbps), 64 KB for LPCM (~340 ms at
    1.5 Mbps). The larger buffer prevents TCP backpressure stalls at
    LPCM's higher bitrate.

25. **Pipeline crash surfacing.** `PipelineRunner` raises a
    `PumpCrashed` event when the pump loop hits an unhandled exception.
    `MainViewModel` subscribes, transitions the state machine to Idle,
    and shows an `InfoBar` with the exception message.

26. **Audio endpoint format monitor.** `AudioEndpointMonitor` polls
    `WasapiLoopbackSource.ProbeMixFormat()` every 5 seconds. If the
    default render endpoint's mix format changes while streaming,
    `PipelineRunner` raises `FormatChanged`; the VM stops the stream
    and shows an error telling the user to restart. This avoids the
    complexity of implementing `IMMNotificationClient` in managed code.

27. **IPv6 SSDP support.** `SsdpDiscovery` scans both
    `239.255.255.250` (IPv4) and `ff02::c` (IPv6 link-local multicast).
    `LocalIpResolver` creates dual-stack sockets when the target is
    IPv6. If the IPv6 scan yields no results, the IPv4 results are
    still returned defensively.

28. **LPCM: WAV header per connection.** `LpcmEncoder` emits only raw
    little-endian 16-bit PCM (no container). The 44-byte RIFF/WAVE
    header is injected by `StreamServer` into **every** new HTTP
    response body, so late-joining or reconnecting Sonos clients always
    receive a valid WAV container header with `0xFFFFFFFF` sentinel
    sizes (streaming mode). AAC has no equivalent requirement because
    ADTS frames are self-describing.

29. **LPCM: No `x-rincon-mp3radio://`; Play timeout handling.**
    `PipelineRunner` passes `useRadioScheme: false` to
    `SonosController.SetUriAndPlayAsync` for LPCM, sending the plain
    `http://` URL. The Play SOAP command is wrapped in a try-catch;
    some Sonos models take >10 s to ACK Play while they buffer the WAV
    stream. Failing to ACK Play is non-fatal — the stream is already
    flowing via SetAVTransportURI.

30. **Sonos HTTP client timeout.** `SonosController` uses a 30 s
     `HttpClient.Timeout` (was 10 s). For WAV streams Sonos may buffer
     several seconds of PCM before responding to SOAP Play, so the
     shorter timeout caused pipeline teardowns that killed working
     streams.

31. **SSDP UDN filtering.** `SsdpDiscovery` rejects devices whose UDN
    doesn't start with `uuid:RINCON_`. This prevents non-Sonos UPnP
    devices (Hue bridges, etc.) from appearing in the speaker list.
    `LookupAsync` (Add by IP) throws `InvalidOperationException` for
    non-Sonos UDNs. The `ScanAsync` path silently skips them.

32. **Per-application capture enumerates only user-facing processes.**
    `EnumerateActiveAudioProcesses` filters out the RoomRelay process
    itself, system services (`svchost`, `dllhost`, etc.), shell/terminal
    windows, browsers (which fail loopback), and anything under
    `C:\Windows`. Only processes with a visible window, a window title,
    or a known media player name are shown. Expired sessions are
    excluded; inactive sessions are included so users can
    pre-select before playback starts.

33. **Process loopback COM errors are user-friendly.** Both the
    `InvalidCastException` (E_NOINTERFACE on `IAudioClient`) during
    activation and during `Initialize` are caught and rethrown as
    `InvalidOperationException` with guidance to switch to "Whole system"
    mode. The pipeline wraps the error again with the app name and PID.

34. **Spectrum analyzer runs before all DSP stages.** The visualizer
    reflects the input signal before gain, EQ, and volume are applied,
    so it shows a consistent view regardless of user slider positions.

35. **Sonos track title via DIDL-Lite metadata.** `BuildSetUriEnvelope`
    accepts an optional `metadataTitle`. When provided, the
    `CurrentURIMetaData` element contains a DIDL-Lite document with
    `dc:title` set to `"RoomRelay — {FriendlyName}"`, so Sonos
    displays the room name instead of a random stream filename.

36. **Speaker list is disabled during streaming.** The `ListView` is
    bound to `IsNotStreaming` so accidental re-selection mid-stream
    can't desync the UI from the core state. `OnSelectedSpeakerChanged`
    reverts the selection if `SetSpeaker` throws.

37. **MenuBar replaced with flyout button.** The top menu bar was
    replaced by a subtle overflow button with a `MenuFlyout`,
    matching the card-based UI. Commands bind directly to VM commands.

## Testing pattern

I/O boundaries are behind interfaces:
- Audio: `IAudioSource` — real impls: `WasapiLoopbackSource`,
  `ProcessLoopbackSource`; fake: `SyntheticSource`
- Network: `ISonosController`, `ISsdpDiscovery`, `ITopologyResolver`,
  `IStreamServer` — real impls are concrete classes registered in DI;
  fakes can be injected for unit tests

Unit tests use xUnit + FluentAssertions + FsCheck. The e2e test
(`MockSonosE2E.cs`) spins up a mock Sonos HTTP+SOAP server on
`127.0.0.1:0`.

## Known gaps (what still needs work)

### Medium priority
- **Fault-injection tests for network layer.** Now that
  `ISsdpDiscovery`, `ITopologyResolver`, and `ISonosController`
  exist, we can add tests that inject timeouts, empty scan results,
  and SOAP 500 responses without spinning up real HTTP servers.

### Low priority
- **Win2D `Microsoft.Graphics.Canvas.dll` AnyCPU vs x64 warning.**
  Suppressed by `RuntimeIdentifier=win-x64`.

- **Serilog log path discoverability.** The log lives at
  `%APPDATA%\RoomRelay\app{yyyymmdd}.log` but isn't shown in the
  UI; an "Open log" menu item would help debugging.

## Package versions

| Package                                 | Version          |
|-----------------------------------------|------------------|
| Microsoft.WindowsAppSDK                 | 2.0.1            |
| CommunityToolkit.Mvvm                   | 8.4.2            |
| H.NotifyIcon.WinUI                      | 2.4.1            |
| Microsoft.Graphics.Win2D                | 1.4.0            |
| MathNet.Numerics                        | 5.0.0            |
| Serilog                                 | 4.3.1            |
| Serilog.Sinks.File                      | 7.0.0            |
| Microsoft.Extensions.DependencyInjection | 10.0.7          |
| Microsoft.Windows.CsWin32               | 0.3.275          |

## Coding conventions

- `Serilog` for logs; exceptions propagate naturally.
- Windows-specific code in Core is not gated behind `#if WINDOWS` — the
  whole project targets `net10.0-windows10.0.26100.0` with
  `SupportedOSPlatformVersion=10.0.19041.0`.
- `unsafe` blocks are allowed (`AllowUnsafeBlocks=true`) for COM and
  Media Foundation interop. Prefer marking individual methods `unsafe`
  rather than the whole class so `await` can still be used elsewhere
  in the file.
- `ReadOnlyMemory<byte>` flows through the encode → broadcast path
  (avoids byte-array copies on the hot path).
- Pages use XAML with `x:Bind` to ViewModels for compile-time binding
  validation and hot reload support. Only custom controls built from
  primitives use imperative C#.
- `.ConfigureAwait(false)` on every `await` in `SonosStreaming.Core`.
- Prefer `Lock` over `object` for mutual exclusion (C# 14 `lock` statement
  works with both).
