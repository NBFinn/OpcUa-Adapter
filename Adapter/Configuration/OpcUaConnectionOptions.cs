namespace OpcUA_Server.Adapter
{
    public sealed class OpcUaConnectionOptions
    {
        public int ConnectionAttempts { get; set; } = 5;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaximumReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
        public bool GetAll { get; set; } = true;
    }
}

