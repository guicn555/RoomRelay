using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SonosStreaming.App.ViewModels;
using SonosStreaming.Core.Pipeline;

namespace SonosStreaming.App.Services;

/// <summary>
/// Polls the audio pipeline at 30 fps and pushes VU / spectrum / client-count
/// values into the MainViewModel so the UI can bind to them declaratively.
/// </summary>
public sealed class SharedGuiBridge : IDisposable
{
    private readonly MainViewModel _vm;
    private PipelineRunner? _pipeline;
    private DispatcherQueue? _dq;
    private PeriodicTimer? _timer;
    private Task? _pollTask;

    public SharedGuiBridge(MainViewModel vm)
    {
        _vm = vm;
    }

    public void Initialize(Window window)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
    }

    public void BindPipeline(PipelineRunner pipeline)
    {
        _pipeline = pipeline;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        _pollTask = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        while (_timer != null && await _timer.WaitForNextTickAsync())
        {
            if (_pipeline == null || _dq == null) continue;

            var vuL = _pipeline.VuMeter.RmsL;
            var vuR = _pipeline.VuMeter.RmsR;
            var clients = _pipeline.ClientCount;
            var spectrum = _pipeline.SpectrumAnalyzer.BandLevels.ToArray();

            _dq.TryEnqueue(() =>
            {
                _vm.VuL = vuL;
                _vm.VuR = vuR;
                _vm.ClientCount = clients;
                _vm.Spectrum = spectrum;
            });
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
