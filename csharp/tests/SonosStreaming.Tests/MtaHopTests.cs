using FluentAssertions;
using SonosStreaming.Core.Audio;
using Xunit;

namespace SonosStreaming.Tests;

// The process-loopback IAudioClient can only be called from the MTA, and the
// app starts and stops capture from the WinUI (STA) thread, so every call on
// it goes through WasapiCaptureBase.RunInMta (issue #36). Real process
// loopback needs an audio device, so these pin the hop itself.
public sealed class MtaHopTests
{
    [Fact]
    public void RunInMta_FromAnSta_RunsTheActionInTheMta()
    {
        ApartmentState? seen = null;
        RunOnSta(() => CaptureSourceProbe.Run(() => seen = Thread.CurrentThread.GetApartmentState()));
        seen.Should().Be(ApartmentState.MTA);
    }

    [Fact]
    public void RunInMta_FromAnSta_RethrowsTheOriginalException()
    {
        Exception? caught = null;
        RunOnSta(() =>
        {
            try { CaptureSourceProbe.Run(() => throw new InvalidOperationException("activation failed")); }
            catch (Exception ex) { caught = ex; }
        });

        // ProcessLoopbackSource's user-facing errors are InvalidOperationException,
        // so the hop must not wrap them in an AggregateException.
        caught.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("activation failed");
    }

    [Fact]
    public void RunInMta_FromAnMta_RunsInline()
    {
        int? actionThread = null;
        int callerThread = 0;
        var t = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            CaptureSourceProbe.Run(() => actionThread = Environment.CurrentManagedThreadId);
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();

        actionThread.Should().Be(callerThread);
    }

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error.Should().BeNull();
    }

    // RunInMta is protected; expose it without touching any audio device.
    private sealed class CaptureSourceProbe : WasapiCaptureBase
    {
        public static void Run(Action action) => RunInMta(action);

        protected override unsafe void ConvertToFloat(byte* src, float[] dst, uint frames) { }
    }
}
