namespace SonosStreaming.Core.Audio;

public enum StreamingFormat
{
    Aac128 = 0,
    Aac192 = 1,
    Aac256 = 2,   // default
    Aac320 = 3,
    WavPcm = 4,   // lossless 16-bit PCM, 48 kHz stereo (~1.5 Mbps), streamed as audio/wav (WAV container, little-endian)
    L16Pcm = 5,   // raw 16-bit PCM, 48 kHz stereo (~1.5 Mbps), streamed as audio/L16 (big-endian/network order)
}

public static class StreamingFormatExtensions
{
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
        StreamingFormat.WavPcm => "WAV PCM lossless (experimental)",
        StreamingFormat.L16Pcm => "L16 PCM low latency (experimental)",
        _ => "AAC 256 kbps",
    };

    public static string ContentType(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm => "audio/wav",
        StreamingFormat.L16Pcm => "audio/L16;rate=48000;channels=2",
        _ => "audio/aac",
    };

    public static string MetadataMimeType(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm => "audio/wav",
        StreamingFormat.L16Pcm => "audio/L16",
        _ => "audio/aac",
    };

    public static string FileExtension(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.WavPcm => ".wav",
        StreamingFormat.L16Pcm => ".l16",
        _ => ".aac",
    };

    public static bool IsPcm(this StreamingFormat fmt) => fmt is StreamingFormat.WavPcm or StreamingFormat.L16Pcm;
}
