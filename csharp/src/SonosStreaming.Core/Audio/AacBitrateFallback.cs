using System.Runtime.InteropServices;
using Serilog;

namespace SonosStreaming.Core.Audio;

/// <summary>
/// Finds the AAC bitrate the Windows AAC encoder MFT will accept.
///
/// Microsoft documents 96, 128, 160 and 192 kbps for stereo, and those are the
/// only rates the MFT lists through <c>GetOutputAvailableType</c>. Windows 11
/// also accepts 256 and 320 kbps when asked for them directly, but Windows 10
/// 22H2 (build 19045) rejects 256 kbps with MF_E_INVALIDMEDIATYPE (issue #35).
/// Enumerating the advertised types would downgrade Windows 11 for no reason,
/// so the encoder asks for the requested rate and steps down until one is
/// accepted.
/// </summary>
public static class AacBitrateFallback
{
    /// <summary>
    /// Stereo bitrates that at least one Windows build accepts, highest first.
    /// 224 and 288 kbps are rejected even on Windows 11, so they are not tried.
    /// </summary>
    public static IReadOnlyList<int> KnownBitRates { get; } =
        [320_000, 256_000, 192_000, 160_000, 128_000, 96_000];

    /// <summary>The requested rate, then every known rate below it, highest first.</summary>
    public static IReadOnlyList<int> Candidates(int requested)
    {
        var candidates = new List<int> { requested };
        foreach (var rate in KnownBitRates)
        {
            if (rate < requested)
                candidates.Add(rate);
        }
        return candidates;
    }

    /// <summary>
    /// Calls <paramref name="apply"/> with each candidate until one does not
    /// throw a <see cref="COMException"/>, and returns that rate. Any other
    /// exception is not a bitrate rejection and propagates immediately.
    /// </summary>
    /// <exception cref="InvalidOperationException">Every candidate was rejected.</exception>
    public static int Negotiate(int requested, Action<int> apply)
    {
        var candidates = Candidates(requested);
        COMException? firstRejection = null;
        foreach (var rate in candidates)
        {
            try
            {
                apply(rate);
            }
            catch (COMException ex)
            {
                firstRejection ??= ex;
                Log.Debug("AAC encoder rejected {Rate} bps: 0x{HResult:X8}", rate, ex.HResult);
                continue;
            }

            if (rate != requested)
            {
                Log.Warning("AAC encoder rejected {Requested} bps (0x{HResult:X8}); using {Rate} bps, the highest rate it accepted",
                    requested, firstRejection!.HResult, rate);
            }
            return rate;
        }

        var tried = string.Join(", ", candidates.Select(r => r / 1000));
        throw new InvalidOperationException(
            $"The Windows AAC encoder on this PC rejected every bitrate RoomRelay tried ({tried} kbps, error 0x{firstRejection!.HResult:X8}). " +
            "Choose WAV PCM lossless instead.",
            firstRejection);
    }
}
