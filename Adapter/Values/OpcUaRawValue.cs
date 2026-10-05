namespace OpcUA_Server.Adapter
{
    public sealed record OpcUaRawValue
    (
        string ServerIdentifier,
        string NodeId,
        object? Value,
        string? DataType,
        DateTimeOffset SourceTimestamp,
        DateTimeOffset ServerTimestamp,
        uint StatusCode,
        bool IsGood
    );
}

