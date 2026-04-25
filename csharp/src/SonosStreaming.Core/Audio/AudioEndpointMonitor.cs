using Serilog;
using SonosStreaming.Core.Audio;

namespace SonosStreaming.Core.Audio;

/// <summary>
/// Monitors the default render endpoint's mix format and raises
/// <see cref="FormatChanged"/> when it changes. Uses a 5-second polling
/// loop so it stays in 100% managed code without implementing COM callbacks.
/// </summary>
public sealed class AudioEndpointMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;
    private MixFormat? _lastFormat;

    public event EventHandler? FormatChanged;

    public AudioEndpointMonitor()
    {
        _pollTask = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, _cts.Token).ConfigureAwait(false);

                var current = WasapiLoopbackSource.ProbeMixFormat();
                if (_lastFormat != null && !_lastFormat.Equals(current))
                {
                    Log.Information("Audio endpoint format changed: {Old} -> {New}", _lastFormat, current);
                    FormatChanged?.Invoke(this, EventArgs.Empty);
                }
                _lastFormat = current;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Format poll failed");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pollTask.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
