namespace SonosStreaming.Core.Audio;

public enum StreamingFormat
{
    Aac128 = 0,
    Aac192 = 1,
    Aac256 = 2,   // default
    Aac320 = 3,
    Lpcm   = 4,   // lossless 16-bit PCM, 48 kHz stereo (~1.5 Mbps), streamed as audio/wav (WAV container, little-endian)
}

public static class StreamingFormatExtensions
{
    public static int Bitrate(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Aac128 => 128_000,
        StreamingFormat.Aac192 => 192_000,
        StreamingFormat.Aac256 => 256_000,
        StreamingFormat.Aac320 => 320_000,
        StreamingFormat.Lpcm   => 0,
        _ => 256_000,
    };

    public static string DisplayName(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Aac128 => "AAC 128 kbps",
        StreamingFormat.Aac192 => "AAC 192 kbps",
        StreamingFormat.Aac256 => "AAC 256 kbps",
        StreamingFormat.Aac320 => "AAC 320 kbps",
        StreamingFormat.Lpcm   => "LPCM/WAV lossless (experimental)",
        _ => "AAC 256 kbps",
    };

    public static string ContentType(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Lpcm => "audio/wav",
        _ => "audio/aac",
    };

    public static string FileExtension(this StreamingFormat fmt) => fmt switch
    {
        StreamingFormat.Lpcm => ".wav",
        _ => ".aac",
    };
}
