namespace OpcUA_Server.Adapter
{
    public sealed record OpcUaSessionEstablished(
        string AdapterName,
        IReadOnlyList<OpcUaRawValue> InitialValues);
}

