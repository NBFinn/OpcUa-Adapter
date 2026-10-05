using OpcUA_Server.Adapter;
using System.Collections.Concurrent;

namespace OpcUA_Server.Adapter;

public sealed class RawValueCache
{
    private readonly ConcurrentDictionary<(string Server, string NodeId), OpcUaRawValue> values = new();

    public event Action<OpcUaRawValue>? ValueUpdated;

    public void Set(OpcUaRawValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        values[(value.ServerIdentifier, value.NodeId)] = value;
        ValueUpdated?.Invoke(value);
    }

    public void Clear() => values.Clear();

    public IReadOnlyList<OpcUaRawValue> GetValues()
    {
        return [.. values.Values.OrderBy(value => value.NodeId)];
    }
}

