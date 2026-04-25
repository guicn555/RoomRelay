using Serilog;

namespace SonosStreaming.Core.Audio;

public static class AdtsFrameScanner
{
    private static readonly uint[] SamplingRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public sealed record AdtsHeader(byte ProfileObjType, byte SamplingFrequencyIndex, byte ChannelConfig, ushort FrameLength)
    {
        public uint SampleRate => SamplingFrequencyIndex < SamplingRates.Length ? SamplingRates[SamplingFrequencyIndex] : 0;
    }

    public static AdtsHeader Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 7)
            throw new FormatException($"ADTS buffer too short ({buf.Length} bytes)");

        ushort sync = (ushort)((buf[0] << 4) | (buf[1] >> 4));
        if (sync != 0xFFF)
            throw new FormatException($"Bad ADTS sync word 0x{sync:X3}");

        byte profileObjType = (byte)((buf[2] >> 6) & 0x03);
        byte samplingFrequencyIndex = (byte)((buf[2] >> 2) & 0x0F);
        byte channelConfig = (byte)(((buf[2] & 0x01) << 2) | ((buf[3] >> 6) & 0x03));
        ushort frameLength = (ushort)((((buf[3] & 0x03) << 11) | (buf[4] << 3) | (buf[5] >> 5)) & 0x1FFF);

        return new AdtsHeader(profileObjType, samplingFrequencyIndex, channelConfig, frameLength);
    }

    public static void WriteHeader(Span<byte> buf, int rawPayloadLen, byte samplingFrequencyIndex, byte channelConfig)
    {
        int frameLength = rawPayloadLen + 7;
        buf[0] = 0xFF;
        buf[1] = 0xF1;
        byte profileBits = 1;
        buf[2] = (byte)((profileBits << 6) | ((samplingFrequencyIndex & 0x0F) << 2) | ((channelConfig >> 2) & 0x01));
        buf[3] = (byte)(((channelConfig & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        buf[4] = (byte)((frameLength >> 3) & 0xFF);
        buf[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        buf[6] = 0xFC;
    }

    public static List<AdtsHeader> ScanFrames(ReadOnlySpan<byte> buf)
    {
        var headers = new List<AdtsHeader>();
        int pos = 0;
        while (pos < buf.Length)
        {
            var h = Parse(buf[pos..]);
            if (h.FrameLength == 0 || pos + h.FrameLength > buf.Length)
                throw new FormatException($"ADTS frame_length {h.FrameLength} overruns buffer at pos {pos}");
            pos += h.FrameLength;
            headers.Add(h);
        }
        return headers;
    }
}
