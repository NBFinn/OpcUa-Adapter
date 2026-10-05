using OpcUA_Server.Adapter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Text;

namespace OpcUA_Server.Adapter;

public sealed class OpcUaAdapter(
    string address,
    RawValueCache? rawValueCache = null,
    ILogger<OpcUaAdapter>? logger = null) :
    IOpcUaAdapter,
    IDisposable
{
    private static readonly ITelemetryContext Telemetry =
        DefaultTelemetry.Create(_ => { });
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> monitoredNodeIds = new(StringComparer.Ordinal);
    private readonly RawValueCache rawValueCache = rawValueCache ?? new RawValueCache();
    private readonly ILogger<OpcUaAdapter> logger = logger ?? NullLogger<OpcUaAdapter>.Instance;
    private ISession? session;
    private Subscription? subscription;
    private int connectionHealthy;
    private int monitoringPublishingInterval = 1_000;
    private bool disposed;

    public bool GetAll;

    public event Action<string, DataValue>? ValueChanged;
    public event Action<OpcUaAdapter, string>? ConnectionLost;
    public event Action<OpcUaAdapter>? ConnectionRestored;

    public string Address { get; set; } = address;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string Name { get; set; } = nameof(OpcUaAdapter);
    public uint SessionTimeout { get; set; } = 60_000;
    public bool UseSecurity { get; set; } = true;
    public bool AcceptUntrustedCertificates { get; set; }
    public bool HasPassword =>
    !string.IsNullOrWhiteSpace(Password);

    public string SecurityMode { get; private set; } = string.Empty;
    public string SecurityPolicy { get; private set; } = string.Empty;
    public bool IsConnected =>
        session?.Connected == true && Volatile.Read(ref connectionHealthy) == 1;
    public int MonitoredNodeCount => monitoredNodeIds.Count;
    public ApplicationConfiguration? Configuration { get; set; }
    public RawValueCache RawValues => rawValueCache;

    public ConnectionState ConnectionState { get; private set; }
    = ConnectionState.Disconnected;

    public event Action<OpcUaAdapter, ConnectionState>? StatusChanged;

    public OpcUaAdapter() : this(string.Empty, null, null) { }

    public async Task ConnectAsync()
    {
        bool connected = false;
        await gate.WaitAsync();

        try
        {
            ThrowIfDisposed();

            if (IsConnected)
            {
                return;
            }
            StatusChanged?.Invoke(this, ConnectionState.Connecting);

            if (!Uri.TryCreate(Address, UriKind.Absolute, out _))
            {
                throw new ArgumentException(
                    "Eine gültige OPC-UA-Serveradresse ist erforderlich.",
                    nameof(Address));
            }

            await CloseSessionAsync();
            ApplicationConfiguration configuration =
                Configuration ?? await CreateConfigurationAsync();
            Configuration = configuration;

            await configuration.ValidateAsync(ApplicationType.Client);

            void certificateHandler(CertificateValidator _, CertificateValidationEventArgs args)
            {
                if (AcceptUntrustedCertificates &&
                    args.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
                {
                    args.Accept = true;
                }
            }

            configuration.CertificateValidator.CertificateValidation += certificateHandler;

            try
            {
                EndpointDescription selectedEndpoint =
                    await CoreClientUtils.SelectEndpointAsync(
                        configuration,
                        Address,
                        UseSecurity,
                        Telemetry,
                        CancellationToken.None)
                    ?? throw new InvalidOperationException(
                        "Es wurde kein passender OPC-UA-Endpoint gefunden.");

                if (UseSecurity &&
                    selectedEndpoint.SecurityMode == MessageSecurityMode.None)
                {
                    throw new InvalidOperationException(
                        "Kein sicherer OPC-UA-Endpoint verfügbar.");
                }

                var endpoint = new ConfiguredEndpoint(
                    null,
                    selectedEndpoint,
                    EndpointConfiguration.Create(configuration));

                IUserIdentity identity = string.IsNullOrWhiteSpace(UserName)
                    ? new UserIdentity()
                    : new UserIdentity(
                        UserName,
                        Encoding.UTF8.GetBytes(Password ?? string.Empty));

                var sessionFactory = new DefaultSessionFactory(Telemetry);

                ISession newSession = await sessionFactory.CreateAsync(
                    configuration,
                    endpoint,
                    false,
                    false,
                    Name,
                    SessionTimeout,
                    identity,
                    null,
                    CancellationToken.None);

                newSession.KeepAliveInterval = 5_000;
                newSession.KeepAlive += OnSessionKeepAlive;
                session = newSession;
                Volatile.Write(ref connectionHealthy, 1);

                if (monitoredNodeIds.Count > 0)
                {
                    await CreateSubscriptionCoreAsync(
                        newSession,
                        [.. monitoredNodeIds],
                        monitoringPublishingInterval);
                }

                connected = true;
                StatusChanged?.Invoke(this, ConnectionState.Connected);
            }
            finally
            {
                configuration.CertificateValidator.CertificateValidation -=
certificateHandler;
            }
        }
        catch
        {
            StatusChanged?.Invoke(this, ConnectionState.Disconnected);
            throw;
        }
        finally
        {
            gate.Release();
        }

        if (connected)
        {
            ConnectionRestored?.Invoke(this);
        }
    }

    public async Task<OpcUaRawValue> ReadAsync(string id)
    {
        var values = await ReadManyAsync([id]);
        return values[0];
    }

    public async Task<IReadOnlyList<OpcUaRawValue>> ReadAllAsync()
    {
        await gate.WaitAsync();

        try
        {
            ISession activeSession = GetConnectedSession();
            List<string> nodeIds = await BrowseVariableNodeIdsAsync(activeSession);
            IList<DataValue> values =
                await ReadManyCoreAsync(activeSession, nodeIds);

            var result = new List<OpcUaRawValue>(
                nodeIds.Count);

            for (int index = 0; index < nodeIds.Count; index++)
            {
                result.Add(StoreRawValue(nodeIds[index], values[index]));
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<OpcUaRawValue>> ReadManyAsync(IList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await gate.WaitAsync();

        try
        {
            ISession activeSession = GetConnectedSession();
            IList<DataValue> values = await ReadManyCoreAsync(activeSession, ids);
            var rawValues = new List<OpcUaRawValue>();
            for (int index = 0; index < ids.Count; index++)
            {
                var rawValue = StoreRawValue(ids[index], values[index]);
                rawValues.Add(rawValue);
            }

            return rawValues;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartMonitoringAllAsync(int publishingInterval = 1_000)
    {
        await gate.WaitAsync();

        try
        {
            ISession activeSession = GetConnectedSession();
            List<string> nodeIds = await BrowseVariableNodeIdsAsync(activeSession);

            monitoredNodeIds.Clear();
            monitoredNodeIds.UnionWith(nodeIds);
            monitoringPublishingInterval = publishingInterval;

            if (nodeIds.Count > 0)
            {
                await CreateSubscriptionCoreAsync(
                    activeSession,
                    nodeIds,
                    publishingInterval);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartMonitoringAsync(
        IEnumerable<string> ids,
        int publishingInterval = 1_000)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await gate.WaitAsync();

        try
        {
            string[] nodeIds = [.. ids.Distinct(StringComparer.Ordinal)];

            if (nodeIds.Length == 0)
            {
                return;
            }

            monitoredNodeIds.Clear();
            monitoredNodeIds.UnionWith(nodeIds);
            monitoringPublishingInterval = publishingInterval;
            ISession activeSession = GetConnectedSession();
            await CreateSubscriptionCoreAsync(
                activeSession,
                nodeIds,
                publishingInterval);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CreateSubscriptionCoreAsync(
        ISession activeSession,
        IReadOnlyCollection<string> nodeIds,
        int publishingInterval)
    {
        if (subscription is not null)
        {
            await subscription.DeleteAsync(true, CancellationToken.None);
            subscription.Dispose();
            subscription = null;
        }

        var newSubscription = new Subscription(activeSession.DefaultSubscription)
        {
            DisplayName = $"{Name}-DataChanges",
            PublishingInterval = publishingInterval,
            KeepAliveCount = 10,
            LifetimeCount = 30,
            PublishingEnabled = true
        };

        var monitoredItems = nodeIds.Select(nodeId =>
            {
                var item = new MonitoredItem(newSubscription.DefaultItem)
                {
                    DisplayName = nodeId,
                    StartNodeId = NodeId.Parse(nodeId),
                    AttributeId = Attributes.Value,
                    SamplingInterval = publishingInterval,
                    QueueSize = 10,
                    DiscardOldest = true
                };
                item.Notification += OnMonitoredItemNotification;
                return item;
            }).ToArray();

        newSubscription.AddItems(monitoredItems);
        activeSession.AddSubscription(newSubscription);
        await newSubscription.CreateAsync(CancellationToken.None);
        subscription = newSubscription;
    }

    private void OnSessionKeepAlive(ISession sender, KeepAliveEventArgs eventArgs)
    {
        if (ServiceResult.IsGood(eventArgs.Status))
        {
            return;
        }

        if (Interlocked.Exchange(ref connectionHealthy, 0) == 0)
        {
            return;
        }

        StatusChanged?.Invoke(this, ConnectionState.Reconnecting);

        string message = eventArgs.Status?.ToString() ?? "Unbekannter KeepAlive-Fehler";
        ConnectionLost?.Invoke(this, message);
    }

    private void OnMonitoredItemNotification(
        MonitoredItem monitoredItem,
        MonitoredItemNotificationEventArgs eventArgs)
    {
        foreach (DataValue dataValue in monitoredItem.DequeueValues())
        {
            try
            {
                string nodeId = monitoredItem.StartNodeId.ToString();
                StoreRawValue(nodeId, dataValue);
                ValueChanged?.Invoke(nodeId, dataValue);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "{ServerName}: Datenänderung konnte nicht verarbeitet werden",
                    Name);
            }
        }
    }

    private OpcUaRawValue StoreRawValue(string nodeId, DataValue dataValue)
    {
        string? dataType = dataValue.WrappedValue.TypeInfo?.BuiltInType.ToString()
            ?? dataValue.Value?.GetType().Name;

        var rawValue = new OpcUaRawValue(
            Name,
            nodeId,
            dataValue.Value,
            dataType,
            ToDateTimeOffset(dataValue.SourceTimestamp),
            ToDateTimeOffset(dataValue.ServerTimestamp),
            dataValue.StatusCode.Code,
            StatusCode.IsGood(dataValue.StatusCode));
        rawValueCache.Set(rawValue);

        return rawValue;
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime timestamp)
    {
        DateTime utcTimestamp = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utcTimestamp);
    }

    private static async Task<List<string>> BrowseVariableNodeIdsAsync(
        ISession activeSession)
    {
        var browser = new Browser(
            activeSession,
            new BrowserOptions
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            })
        {
            ContinueUntilDone = true
        };

        var pendingNodes = new Stack<NodeId>();
        var visitedNodes = new HashSet<NodeId>();
        var variableIds = new HashSet<string>(StringComparer.Ordinal);

        pendingNodes.Push(ObjectIds.ObjectsFolder);

        while (pendingNodes.Count > 0)
        {
            NodeId currentNode = pendingNodes.Pop();

            if (!visitedNodes.Add(currentNode))
            {
                continue;
            }

            ReferenceDescriptionCollection references =
                await browser.BrowseAsync(currentNode, CancellationToken.None);

            foreach (ReferenceDescription reference in references)
            {
                NodeId? nodeId = ExpandedNodeId.ToNodeId(
                    reference.NodeId,
                    activeSession.NamespaceUris);

                if (nodeId is null || nodeId.NamespaceIndex == 0)
                {
                    continue;
                }

                if (reference.NodeClass == NodeClass.Variable)
                {
                    variableIds.Add(nodeId.ToString());
                }
                else if (reference.NodeClass == NodeClass.Object)
                {
                    pendingNodes.Push(nodeId);
                }
            }
        }

        return [.. variableIds.OrderBy(id => id, StringComparer.Ordinal)];
    }

    private static async Task<IList<DataValue>> ReadManyCoreAsync(
        ISession activeSession,
        IList<string> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var nodes = new ReadValueIdCollection();

        foreach (string id in ids)
        {
            nodes.Add(new ReadValueId
            {
                NodeId = NodeId.Parse(id),
                AttributeId = Attributes.Value
            });
        }

        ReadResponse response = await activeSession.ReadAsync(
            null,
            0,
            TimestampsToReturn.Both,
            nodes,
            CancellationToken.None);

        if (response.Results.Count != ids.Count)
        {
            throw new InvalidOperationException(
                "Der OPC-UA-Server lieferte eine unvollständige Leseantwort.");
        }

        return [.. response.Results];
    }

    public async Task WriteAsync(string id, object value)
    {
        await gate.WaitAsync();

        try
        {
            ISession activeSession = GetConnectedSession();
            var nodes = new WriteValueCollection
            {
                new()
                {
                    NodeId = NodeId.Parse(id),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };

            WriteResponse response = await activeSession.WriteAsync(
                null,
                nodes,
                CancellationToken.None);

            if (response.Results.Count != 1)
            {
                throw new InvalidOperationException(
                    "Der OPC-UA-Server lieferte eine ungültige Schreibantwort.");
            }

            if (!StatusCode.IsGood(response.Results[0]))
            {
                throw new ServiceResultException(response.Results[0]);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Disconnect()
    {
        await gate.WaitAsync();

        try
        {
            await CloseSessionAsync();
            StatusChanged?.Invoke(this, ConnectionState.Disconnected);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Wait();

        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            DisposeSession();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ApplicationConfiguration> CreateConfigurationAsync()
    {
        var application = new ApplicationInstance(Telemetry)
        {
            ApplicationName = Name,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = new ApplicationConfiguration(Telemetry)
            {
                ApplicationName = Name
            }
        };

        var builder = new ApplicationConfigurationBuilder(application);
        builder.AsClient();
        builder.AddSecurityConfiguration(Name);

        ApplicationConfiguration configuration =
            await builder.CreateAsync(CancellationToken.None);

        // Separate certificate stores for this independent copy.
        string pkiRoot = Path.Combine(AppContext.BaseDirectory, "OpcUA_Server", "AdapterPki");
        configuration.SecurityConfiguration.ApplicationCertificate.StorePath = Path.Combine(pkiRoot, "own");
        configuration.SecurityConfiguration.TrustedPeerCertificates.StorePath = Path.Combine(pkiRoot, "trusted");
        configuration.SecurityConfiguration.TrustedIssuerCertificates.StorePath = Path.Combine(pkiRoot, "issuers");
        configuration.SecurityConfiguration.RejectedCertificateStore.StorePath = Path.Combine(pkiRoot, "rejected");

        if (!await application.CheckApplicationInstanceCertificatesAsync(false))
        {
            throw new InvalidOperationException(
                "Das OPC-UA-Anwendungszertifikat konnte nicht erstellt werden.");
        }

        return configuration;
    }

    private ISession GetConnectedSession()
    {
        ThrowIfDisposed();

        if (session?.Connected != true)
        {
            throw new InvalidOperationException(
                "Der OPC-UA-Client ist nicht verbunden.");
        }

        return session;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OpcUaAdapter));
    }

    private async Task CloseSessionAsync()
    {
        ISession? previousSession = session;
        session = null;
        Volatile.Write(ref connectionHealthy, 0);
        Subscription? previousSubscription = subscription;
        subscription = null;

        if (previousSubscription is not null)
        {
            try
            {
                if (previousSubscription.Created)
                {
                    await previousSubscription.DeleteAsync(true, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "{ServerName}: Alte Subscription konnte beim Trennen nicht sauber gelöscht werden",
                    Name);
            }
            finally
            {
                previousSubscription.Dispose();
            }
        }

        if (previousSession is null)
        {
            return;
        }

        try
        {
            previousSession.KeepAlive -= OnSessionKeepAlive;
            if (previousSession.Connected)
            {
                await previousSession.CloseAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "{ServerName}: Alte Session konnte beim Trennen nicht sauber geschlossen werden",
                Name);
        }
        finally
        {
            previousSession.Dispose();
        }
    }

    private void DisposeSession()
    {
        subscription?.Dispose();
        subscription = null;
        ISession? previousSession = session;
        session = null;
        Volatile.Write(ref connectionHealthy, 0);
        if (previousSession is not null) previousSession.KeepAlive -= OnSessionKeepAlive;
        previousSession?.Dispose();
    }
}


