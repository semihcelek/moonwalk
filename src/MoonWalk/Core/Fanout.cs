using System.Threading.Channels;

namespace MoonWalk.Core;

/// <summary>Publishes each item to every current subscriber. Subscribers read from an unbounded channel.</summary>
public sealed class Fanout<T>
{
    private readonly object _lock = new();
    private readonly List<Channel<T>> _subscribers = new();

    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>();
        lock (_lock)
        {
            _subscribers.Add(channel);
        }
        return new Subscription(this, channel);
    }

    public void Publish(T item)
    {
        lock (_lock)
        {
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryWrite(item);
            }
        }
    }

    private void Remove(Channel<T> channel)
    {
        lock (_lock)
        {
            _subscribers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(Fanout<T> owner, Channel<T> channel) : IDisposable
    {
        public ChannelReader<T> Reader => channel.Reader;

        public void Dispose() => owner.Remove(channel);
    }
}
