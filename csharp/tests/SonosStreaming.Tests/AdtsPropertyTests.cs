using SonosStreaming.Core.Audio;
using FluentAssertions;
using FsCheck.Xunit;
using Xunit;

namespace SonosStreaming.Tests;

public class AdtsPropertyTests
{
    [Property]
    public void WriteThenParse_Roundtrips(int payloadLen, byte freqIdx, byte chConfig)
    {
        payloadLen = Math.Abs(payloadLen) % 8192;
        freqIdx = (byte)(Math.Abs(freqIdx) % 13);
        chConfig = (byte)(Math.Abs(chConfig) % 7 + 1);

        var header = new byte[7];
        AdtsFrameScanner.WriteHeader(header, payloadLen, freqIdx, chConfig);

        var frame = header.Concat(new byte[payloadLen]).ToArray();
        var h = AdtsFrameScanner.Parse(frame);

        h.ProfileObjType.Should().Be(1);
        h.SamplingFrequencyIndex.Should().Be(freqIdx);
        h.ChannelConfig.Should().Be(chConfig);
        h.FrameLength.Should().Be((ushort)(payloadLen + 7));
    }

    [Fact]
    public void Scan_MultipleFrames_AllParsed()
    {
        var buf = new byte[0];
        for (int i = 0; i < 5; i++)
        {
            var header = new byte[7];
            AdtsFrameScanner.WriteHeader(header, 64, 3, 2);
            buf = buf.Concat(header).Concat(new byte[64]).ToArray();
        }

        var headers = AdtsFrameScanner.ScanFrames(buf);
        headers.Should().HaveCount(5);
        foreach (var h in headers)
        {
            h.SampleRate.Should().Be(48000);
            h.ChannelConfig.Should().Be(2);
        }
    }

    [Fact]
    public void Parse_BadSync_Throws()
    {
        var buf = new byte[7];
        buf[0] = 0x00;
        var act = () => AdtsFrameScanner.Parse(buf);
        act.Should().Throw<FormatException>();
    }
}
