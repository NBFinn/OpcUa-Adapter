using OpcUA_Server.Adapter;

namespace OpcUA_Server.Adapter
{
    public sealed record OpcUaConnectionChanged(
        string Source,
        ConnectionState CommunicationState);
}
