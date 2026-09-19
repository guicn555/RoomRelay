namespace SonosStreaming.Core.Audio;

public enum StreamingFormat
{
    Aac128 = 0,
    Aac192 = 1,
    Aac256 = 2,   // default
    Aac320 = 3,
    WavPcm = 4,   // lossless 16-bit PCM, 48 kHz stereo (~1.5 Mbps), streamed as audio/wav (WAV container, little-endian)

    /// <summary>
    /// Retired. Sonos does not implement <c>audio/L16</c>: querying
    /// ConnectionManager GetProtocolInfo on a One SL, a Beam and an Arc returns
    /// 65 sink formats and none of them is audio/L16, so the speaker connected,
    /// read one chunk while trying to identify the stream, and closed. See
    /// issue #32.
    ///
    /// The member is kept so an existing settings.json still deserializes.
    /// <see cref="StreamingFormatExtensions.Normalize"/> maps it to
    /// <see cref="WavPcm"/> on load, and the mappings below deliberately
    /// resolve it to the WAV behaviour so that any path which somehow misses
    /// the normalization degrades to a format that works rather than silence.
    /// </summary>
    L16Pcm = 5,
}

public static class StreamingFormatExtensions
{
    /// <summary>
    /// Replaces retired formats with the supported equivalent. Call this on any
    /// value that came from persisted settings.
    /// </summary>
    public static StreamingFormat Normalize(this StreamingFormat fmt) =>
        fmt == StreamingFormat.L16Pcm ? StreamingFormat.WavPcm : fmt;

    /// <summary>Formats offered in the UI. Retired values are not included.</summary>
    public static IReadOnlyList<StreamingFormat> Selectable { get; } = new[]
    {
        StreamingFormat.Aac128,
        StreamingFormat.Aac192,
        StreamingFormat.Aac256,
        StreamingFormat.Aac320,
        StreamingFormat.WavPcm,
    };

    public static int Bitrate(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Aac128 => 128_000,
        StreamingFormat.Aac192 => 192_000,
        StreamingFormat.Aac256 => 256_000,
        StreamingFormat.Aac320 => 320_000,
        StreamingFormat.WavPcm => 0,
        StreamingFormat.L16Pcm => 0,
        _ => 256_000,
    };

    public static string DisplayName(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Aac128 => "AAC 128 kbps",
        StreamingFormat.Aac192 => "AAC 192 kbps",
        StreamingFormat.Aac256 => "AAC 256 kbps",
        StreamingFormat.Aac320 => "AAC 320 kbps",
        StreamingFormat.WavPcm => "WAV PCM lossless",
        StreamingFormat.L16Pcm => "WAV PCM lossless",
        _ => "AAC 256 kbps",
    };

    public static string ContentType(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm or StreamingFormat.L16Pcm => "audio/wav",
        _ => "audio/aac",
    };

    public static string MetadataMimeType(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm or StreamingFormat.L16Pcm => "audio/wav",
        _ => "audio/aac",
    };

    public static string FileExtension(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm or StreamingFormat.L16Pcm => ".wav",
        _ => ".aac",
    };

    public static bool IsPcm(this StreamingFormat fmt) => fmt is StreamingFormat.WavPcm or StreamingFormat.L16Pcm;
}
