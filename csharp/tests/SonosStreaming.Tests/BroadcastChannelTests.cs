using FluentAssertions;
using SonosStreaming.Core.Network;
using Xunit;

namespace SonosStreaming.Tests;

public sealed class BroadcastChannelTests
{
    [Fact]
    public void Write_WhenSubscriberBacklogIsFull_DropsSubscriberInsteadOfOldAudio()
    {
        var broadcast = new BroadcastChannel<int>(capacity: 1);
        var subscriber = broadcast.Subscribe();

        broadcast.Write(1);
        broadcast.Write(2);

        broadcast.DroppedSubscribers.Should().Be(1);
        broadcast.SubscriberCount.Should().Be(0);
        subscriber.Reader.TryRead(out var value).Should().BeTrue();
        value.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_RemovesSubscriberAndCompletesIt()
    {
        var broadcast = new BroadcastChannel<int>(capacity: 4);
        var subscriber = broadcast.Subscribe();

        broadcast.Unsubscribe(subscriber);

        broadcast.SubscriberCount.Should().Be(0);
        subscriber.Writer.TryWrite(1).Should().BeFalse();
    }

    [Fact]
    public void Unsubscribe_StopsDeliveringAndNeverCountsAsSlow()
    {
        // An abandoned subscription used to keep filling until its buffer was
        // full, then get logged as a slow client (issue #26).
        var broadcast = new BroadcastChannel<int>(capacity: 1);
        var live = broadcast.Subscribe();
        var abandoned = broadcast.Subscribe();

        broadcast.Unsubscribe(abandoned);
        for (int i = 0; i < 10; i++)
        {
            broadcast.Write(i);
            live.Reader.TryRead(out _);
        }

        broadcast.DroppedSubscribers.Should().Be(0);
        broadcast.SubscriberCount.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_IsIdempotent()
    {
        var broadcast = new BroadcastChannel<int>(capacity: 4);
        var subscriber = broadcast.Subscribe();

        broadcast.Unsubscribe(subscriber);
        broadcast.Unsubscribe(subscriber);

        broadcast.SubscriberCount.Should().Be(0);
    }
}
