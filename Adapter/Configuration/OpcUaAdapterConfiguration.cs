namespace OpcUA_Server.Adapter
{
    public class OpcUaAdapterConfiguration
    {
        public string Name { get; init; } = string.Empty;
        public string EndpointUrl { get; init; } = string.Empty;
        public string FallbackEndpointUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool AnonymousAccess { get; set; } = true;
        public bool UseSecurity { get; set; } = false;
        public OpcUaConnectionOptions ConnectionOptions { get; set; } = new();
        public List<string> NodeIds { get; set; } = [];
        public int SubscriptionIntervalMilliseconds { get; set; } = 1_000;
    }
}

