using SonosStreaming.Core.Network;

namespace SonosStreaming.Core.State;

public enum AppState
{
    Idle,
    Starting,
    Streaming,
    Stopping,
}

public enum AudioSourceSelection
{
    WholeSystem,
    Process,
}

public sealed class AudioSourceProcessSelection
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
}

public sealed class Selection
{
    public AudioSourceSelection Source { get; set; } = AudioSourceSelection.WholeSystem;
    public AudioSourceProcessSelection? ProcessSelection { get; set; }
    public SonosDevice? Speaker { get; set; }
    public List<SonosDevice> Discovered { get; set; } = new();
}

public sealed class AppCore
{
    private readonly object _lock = new();
    private AppState _state = AppState.Idle;
    private Selection _selection = new();

    public AppState State { get { lock (_lock) return _state; } }
    public Selection Selection { get { lock (_lock) return _selection; } }

    public void SetSource(AudioSourceSelection source, AudioSourceProcessSelection? process = null)
    {
        lock (_lock)
        {
            if (_state != AppState.Idle)
                throw new InvalidOperationException("Cannot change source while streaming");
            _selection.Source = source;
            _selection.ProcessSelection = process;
        }
    }

    public void SetSpeaker(SonosDevice speaker)
    {
        lock (_lock)
        {
            if (_state != AppState.Idle)
                throw new InvalidOperationException("Cannot change speaker while streaming");
            _selection.Speaker = speaker;
        }
    }

    public void SetDiscovered(List<SonosDevice> devices)
    {
        lock (_lock) { _selection.Discovered = devices; }
    }

    public bool CanStart
    {
        get
        {
            lock (_lock)
            {
                return _state == AppState.Idle &&
                       _selection.Speaker != null &&
                       (_selection.Source != AudioSourceSelection.Process || _selection.ProcessSelection != null);
            }
        }
    }

    public bool CanStop
    {
        get { lock (_lock) return _state == AppState.Streaming; }
    }

    public void BeginStart()
    {
        lock (_lock)
        {
            if (!CanStart)
                throw new InvalidOperationException($"Cannot start from state {_state}");
            _state = AppState.Starting;
        }
    }

    public void FinishStart()
    {
        lock (_lock)
        {
            if (_state != AppState.Starting)
                throw new InvalidOperationException($"finish_start called from {_state}");
            _state = AppState.Streaming;
        }
    }

    public void BeginStop()
    {
        lock (_lock)
        {
            if (_state != AppState.Streaming && _state != AppState.Starting)
                throw new InvalidOperationException($"Cannot stop from state {_state}");
            _state = AppState.Stopping;
        }
    }

    public void FinishStop()
    {
        lock (_lock)
        {
            if (_state != AppState.Stopping)
                throw new InvalidOperationException($"finish_stop called from {_state}");
            _state = AppState.Idle;
        }
    }
}
