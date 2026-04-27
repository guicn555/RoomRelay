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
}
