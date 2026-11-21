using System.Runtime.InteropServices;

namespace SonosStreaming.Core.Audio;

/// <summary>
/// Recognises the errors Windows gives when Media Foundation is not installed:
/// N and KN editions without the Media Feature Pack, and Windows Server without
/// the Media Foundation feature. MFPlat.dll is missing there, so the first MF
/// P/Invoke throws <see cref="DllNotFoundException"/>, and an MFT whose DLL is
/// missing fails CoCreateInstance with REGDB_E_CLASSNOTREG.
/// </summary>
public static class MediaFoundationAvailability
{
    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    public const string MissingMessage =
        "Windows Media Foundation is not installed on this PC, and RoomRelay needs it to encode AAC " +
        "(and to resample audio when the output device does not run at 48 kHz). " +
        "On Windows N and KN editions, install the Media Feature Pack from Settings > Apps > Optional features. " +
        "On Windows Server, add the Media Foundation feature.";

    public static bool IsMissing(Exception ex) =>
        ex is DllNotFoundException || ex is COMException { HResult: REGDB_E_CLASSNOTREG };
}
