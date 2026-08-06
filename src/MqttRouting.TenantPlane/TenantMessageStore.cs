using System.Collections.Concurrent;

namespace MqttRouting.TenantPlane;

internal sealed class TenantMessageStore
{
    private readonly ConcurrentQueue<TenantMessage> _messages = new();

    public void Add(TenantMessage message) => _messages.Enqueue(message);

    public IReadOnlyCollection<TenantMessage> All() => _messages.ToArray();
}
