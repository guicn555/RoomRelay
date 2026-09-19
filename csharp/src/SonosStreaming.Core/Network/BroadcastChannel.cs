using System.Threading.Channels;
using Serilog;

namespace SonosStreaming.Core.Network;

public sealed class BroadcastChannel<T>
{
    private readonly int _capacity;
    private readonly List<Channel<T>> _subscribers = new();
    private readonly object _lock = new();
    private long _droppedSubscribers;

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

    public long DroppedSubscribers => Interlocked.Read(ref _droppedSubscribers);

    public Channel<T> Subscribe()
    {
        var ch = Channel.CreateBounded<T>(new BoundedChannelOptions(_capacity)
        {
            // Do not silently drop audio chunks. For PCM especially, gaps in
            // the byte stream become audible corruption, so slow clients are
            // disconnected and logged instead.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        lock (_lock)
        {
            _subscribers.Add(ch);
        }
        return ch;
    }

    /// <summary>
    /// Detaches a subscription. Callers must do this when their client goes
    /// away: an abandoned subscription keeps receiving chunks until its buffer
    /// fills, which inflates <see cref="SubscriberCount"/>, pins up to
    /// <c>capacity</c> chunks of audio per dead client, and eventually logs a
    /// misleading "slow subscriber" warning (issue #26).
    /// </summary>
    public void Unsubscribe(Channel<T> subscription)
    {
        bool removed;
        lock (_lock) { removed = _subscribers.Remove(subscription); }
        if (removed) subscription.Writer.TryComplete();
    }

    public void Write(T item)
    {
        List<Channel<T>>? toRemove = null;
        lock (_lock)
        {
            foreach (var sub in _subscribers)
            {
                if (sub.Writer.TryWrite(item)) continue;
                sub.Writer.TryComplete();
                (toRemove ??= new List<Channel<T>>()).Add(sub);
                Interlocked.Increment(ref _droppedSubscribers);
            }

            if (toRemove != null)
            {
                foreach (var sub in toRemove)
                    _subscribers.Remove(sub);
            }
        }

        if (toRemove != null)
        {
            Log.Warning("Dropped {Count} slow stream subscriber(s); total dropped={Total}",
                toRemove.Count, DroppedSubscribers);
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
