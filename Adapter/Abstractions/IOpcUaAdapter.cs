using Opc.Ua;

namespace OpcUA_Server.Adapter;

public interface IOpcUaAdapter
{
    event Action<string, DataValue>? ValueChanged;

    string Name { get; }

    string Address { get; }

    bool IsConnected { get; }

    string? UserName { get; }

    bool HasPassword { get; }

    bool UseSecurity { get; }

    string SecurityMode { get; }

    string SecurityPolicy { get; }

    Task ConnectAsync();

    Task Disconnect();

    Task<IReadOnlyList<OpcUaRawValue>> ReadAllAsync();

    Task<OpcUaRawValue> ReadAsync(string id);

    Task WriteAsync(string id, object value);

    Task StartMonitoringAsync(
        IEnumerable<string> ids,
        int publishingInterval = 1_000);
}

