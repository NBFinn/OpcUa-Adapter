using OpcUA_Server.Adapter;
using MassTransit.Mediator;

using Microsoft.Extensions.Logging;

using Opc.Ua;
using System.Net.Sockets;

namespace OpcUA_Server.Adapter
{
    public class OpcUaSessionManager(
        IEnumerable<OpcUaAdapterConfiguration> configurations,
        RawValueCache rawValues,
        IMediator mediator,
        ILogger<OpcUaSessionManager> logger,
        ILoggerFactory loggerFactory) : global::OpcUA_Server.Adapter.IOpcUaSessionManager
    {
        private readonly IReadOnlyList<OpcUaAdapterConfiguration> configurations = [.. configurations];
        private readonly Dictionary<string, OpcUaAdapter> adapters =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task> reconnectTasks = [];
        private readonly RawValueCache rawValues = rawValues;
        private readonly IMediator mediator = mediator;
        private readonly ILogger<OpcUaSessionManager> logger = logger;
        private readonly ILoggerFactory loggerFactory = loggerFactory;
        private CancellationTokenSource? reconnectCancellation;

        public IReadOnlyDictionary<string, global::OpcUA_Server.Adapter.IOpcUaAdapter> Adapters =>
            adapters.ToDictionary(
                item => item.Key,
                item => (global::OpcUA_Server.Adapter.IOpcUaAdapter)item.Value,
                StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyList<string>> ConfiguredNodeIds =>
            configurations.ToDictionary(
                configuration => configuration.Name,
                configuration => (IReadOnlyList<string>)configuration.NodeIds,
                StringComparer.OrdinalIgnoreCase);

        public async Task Start(
            CancellationToken cancellationToken = default)
        {
            if (adapters.Count > 0) return;

            reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

            foreach (OpcUaAdapterConfiguration configuration in configurations)
            {
                var adapter = new OpcUaAdapter(
                    configuration.EndpointUrl,
                    rawValues,
                    loggerFactory.CreateLogger<OpcUaAdapter>())
                {
                    Name = configuration.Name,
                    UserName = configuration.AnonymousAccess ? null : configuration.Username,
                    Password = configuration.AnonymousAccess ? null : configuration.Password,
                    UseSecurity = configuration.UseSecurity,
                    AcceptUntrustedCertificates = true,
                    GetAll = configuration.ConnectionOptions.GetAll
                };

                adapter.ConnectionLost += (_, reason) =>
                    logger.LogWarning(
                        "{ServerName}: Verbindung verloren: {Reason}",
                        configuration.Name,
                        reason);

                adapters.Add(configuration.Name, adapter);
                adapter.StatusChanged += OnAdapterStatusChanged;
            }

            await Task.WhenAll(adapters.Values.Select(adapter =>
                ConnectWithRetryAsync(
                    adapter,
                    GetOptions(adapter.Name),
                    reconnectCancellation.Token)));

            rawValues.ValueUpdated -= OnRawValueChanged;
            rawValues.ValueUpdated += OnRawValueChanged;

            foreach (OpcUaAdapter adapter in adapters.Values)
            {
                reconnectTasks.Add(MonitorConnectionAsync(
                    adapter,
                    GetOptions(adapter.Name),
                    reconnectCancellation.Token));
            }
        }

        private async void OnRawValueChanged(OpcUaRawValue value)
        {
            try
            {
                await mediator.Publish(new OpcUaRawValueChanged(value));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "{ServerName}: Wertänderung konnte nicht verarbeitet werden",
                    value.ServerIdentifier);
            }
        }

        private async Task<bool> ConnectWithRetryAsync(
            OpcUaAdapter adapter,
            OpcUaConnectionOptions options,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            OpcUaAdapterConfiguration configuration = GetConfiguration(adapter.Name);
            List<string> endpointUrls = [configuration.EndpointUrl];

            if (!string.IsNullOrWhiteSpace(configuration.FallbackEndpointUrl) &&
                !configuration.FallbackEndpointUrl.Equals(
                    configuration.EndpointUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                endpointUrls.Add(configuration.FallbackEndpointUrl);
            }

            foreach (string endpointUrl in endpointUrls)
            {
                if (!await IsEndpointReachableAsync(endpointUrl, cancellationToken))
                {
                    lastException = new InvalidOperationException(
                        $"Der OPC-UA-Endpoint '{endpointUrl}' ist nicht erreichbar.");
                    continue;
                }

                adapter.Address = endpointUrl;

                for (int attempt = 1; attempt <= options.ConnectionAttempts; attempt++)
                {
                    try
                    {
                        await adapter.ConnectAsync();

                        await ReadInitialValuesAsync(adapter, options, cancellationToken);

                        if (logger.IsEnabled(LogLevel.Information))
                        {
                            logger.LogInformation(
                                "{ServerName}: mit {EndpointUrl} verbunden; {NodeCount} Nodes per Subscription überwacht",
                                adapter.Name, endpointUrl, adapter.MonitoredNodeCount);
                        }
                        return true;
                    }
                    catch (Exception exception)
                    {
                        lastException = exception;

                        if (attempt < options.ConnectionAttempts)
                        {
                            await Task.Delay(options.RetryDelay, cancellationToken);
                        }
                    }
                }
            }

            logger.LogError(
                lastException,
                "{ServerName}: Verbindung fehlgeschlagen",
                adapter.Name);
            return false;
        }

        private async Task ReadInitialValuesAsync(OpcUaAdapter adapter, OpcUaConnectionOptions options, CancellationToken cancellationToken)
        {
            OpcUaAdapterConfiguration configuration = GetConfiguration(adapter.Name);
            IReadOnlyList<OpcUaRawValue> initialValues;
            await StartMonitoringAsync(adapter, options.GetAll);
            if (options.GetAll)
            {
                initialValues = await adapter.ReadAllAsync();
            }
            else
            {
                initialValues = await adapter.ReadManyAsync(configuration.NodeIds);
            }

            await mediator.Publish(
                new OpcUaSessionEstablished(adapter.Name, initialValues),
                cancellationToken);
        }

        private async Task StartMonitoringAsync(OpcUaAdapter adapter, bool getAll)
        {
            OpcUaAdapterConfiguration configuration = GetConfiguration(adapter.Name);
            if (getAll)
            {
                await adapter.StartMonitoringAllAsync(
                    configuration.SubscriptionIntervalMilliseconds);
            }
            else
            {
                await adapter.StartMonitoringAsync(
                    configuration.NodeIds,
                    configuration.SubscriptionIntervalMilliseconds);
            }
        }
        private static async Task<bool> IsEndpointReachableAsync(
            string endpointUrl,
            CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpoint))
            {
                return false;
            }

            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));

            try
            {
                await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
                return true;
            }
            catch (Exception exception) when (
                exception is SocketException or OperationCanceledException)
            {
                return false;
            }
        }

        private async Task MonitorConnectionAsync(
            OpcUaAdapter adapter,
            OpcUaConnectionOptions options,
            CancellationToken cancellationToken)
        {
            TimeSpan reconnectDelay = options.InitialReconnectDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(reconnectDelay, cancellationToken);

                    if (adapter.IsConnected)
                    {
                        reconnectDelay = options.InitialReconnectDelay;
                        continue;
                    }

                    bool connected = await ConnectWithRetryAsync(
                        adapter,
                        options,
                        cancellationToken);
                    reconnectDelay = connected
                        ? options.InitialReconnectDelay
                        : TimeSpan.FromMilliseconds(Math.Min(
                            reconnectDelay.TotalMilliseconds * 2,
                            options.MaximumReconnectDelay.TotalMilliseconds));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    reconnectDelay = TimeSpan.FromMilliseconds(Math.Min(
                        reconnectDelay.TotalMilliseconds * 2,
                        options.MaximumReconnectDelay.TotalMilliseconds));
                    logger.LogError(
                        exception,
                        "{ServerName}: Reconnect fehlgeschlagen",
                        adapter.Name);
                }
            }
        }

        private async void OnAdapterStatusChanged(OpcUaAdapter adapter, ConnectionState connectionState)
        {
            try
            {
                await mediator.Publish(
                    new OpcUaConnectionChanged(adapter.Name, connectionState));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "{ServerName}: Statusänderung konnte nicht verarbeitet werden",
                    adapter.Name);
            }
        }

        public async Task Stop()
        {
            if (reconnectCancellation is not null)
            {
#if NET8_0_OR_GREATER
                await reconnectCancellation.CancelAsync();
#else
                reconnectCancellation.Cancel();
#endif

                try
                {
                    await Task.WhenAll(reconnectTasks);
                }
                catch (OperationCanceledException)
                {
                }

                reconnectTasks.Clear();
                reconnectCancellation.Dispose();
                reconnectCancellation = null;
            }

            foreach (OpcUaAdapter adapter in adapters.Values)
            {
                await adapter.Disconnect();
                adapter.Dispose();
            }

            adapters.Clear();
            rawValues.Clear();
            rawValues.ValueUpdated -= OnRawValueChanged;
        }

        private OpcUaConnectionOptions GetOptions(string serverName)
        {
            return GetConfiguration(serverName).ConnectionOptions;
        }

        private OpcUaAdapterConfiguration GetConfiguration(string serverName)
        {
            return configurations.First(configuration =>
                configuration.Name.Equals(
                    serverName,
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}


