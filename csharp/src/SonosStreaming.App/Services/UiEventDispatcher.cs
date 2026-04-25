using Microsoft.UI.Dispatching;
using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;

namespace SonosStreaming.App.Services;

public sealed class UiEventDispatcher
{
    private readonly AppCore _core;
    private readonly PipelineRunner _pipeline;
    private readonly DispatcherQueue _dq;

    public UiEventDispatcher(AppCore core, PipelineRunner pipeline)
    {
        _core = core;
        _pipeline = pipeline;
        _dq = DispatcherQueue.GetForCurrentThread();
    }
}
