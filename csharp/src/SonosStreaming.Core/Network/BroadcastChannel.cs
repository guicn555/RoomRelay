using System.Threading.Channels;

namespace SonosStreaming.Core.Network;

public sealed class BroadcastChannel<T>
{
    private readonly int _capacity;
    private readonly List<Channel<T>> _subscribers = new();
    private readonly object _lock = new();

    public BroadcastChannel(int capacity = 64)
    {
        _capacity = capacity;
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock) return _subscribers.Count;
        }
    }

    public Channel<T> Subscribe()
    {
        var ch = Channel.CreateBounded<T>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock)
        {
            _subscribers.Add(ch);
        }
        return ch;
    }

    public void Write(T item)
    {
        List<Channel<T>> snapshot;
        lock (_lock) { snapshot = new List<Channel<T>>(_subscribers); }

        var toRemove = new List<Channel<T>>();
        foreach (var sub in snapshot)
        {
            if (!sub.Writer.TryWrite(item))
            {
                sub.Writer.TryComplete();
                toRemove.Add(sub);
            }
        }

        if (toRemove.Count > 0)
        {
            lock (_lock)
            {
                foreach (var sub in toRemove)
                    _subscribers.Remove(sub);
            }
        }
    }

    public void CompleteAll()
    {
        lock (_lock)
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
}
