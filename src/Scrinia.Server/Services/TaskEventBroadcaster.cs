using System.Collections.Concurrent;
using System.Threading.Channels;
using Scrinia.Server.Models;

namespace Scrinia.Server.Services;

public sealed class TaskEventBroadcaster
{
    private readonly ConcurrentDictionary<string, Channel<TaskEvent>> _subscribers = new();

    public string Subscribe()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _subscribers[id] = Channel.CreateBounded<TaskEvent>(100);
        return id;
    }

    public void Unsubscribe(string id) => _subscribers.TryRemove(id, out _);

    public void Broadcast(TaskEvent evt)
    {
        foreach (var (id, channel) in _subscribers)
        {
            if (!channel.Writer.TryWrite(evt))
                _subscribers.TryRemove(id, out _); // evict dead subscribers
        }
    }

    public ChannelReader<TaskEvent> GetReader(string id) =>
        _subscribers.TryGetValue(id, out var ch) ? ch.Reader : Channel.CreateUnbounded<TaskEvent>().Reader;
}
