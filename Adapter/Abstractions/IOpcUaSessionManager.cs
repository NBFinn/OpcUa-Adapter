namespace OpcUA_Server.Adapter;

public interface IOpcUaSessionManager
{
    IReadOnlyDictionary<string, IOpcUaAdapter> Adapters { get; }

    IReadOnlyDictionary<string, IReadOnlyList<string>> ConfiguredNodeIds { get; }

    Task Start(CancellationToken cancellationToken = default);

    Task Stop();
}

