# OPC UA Adapter

Reusable OPC UA client source code for connecting, reading, writing and monitoring node values. This repository contains twelve C# source files and this guide. It is a source package, not a standalone application or a prebuilt DLL.

The companion server and dashboard are available in [NBFinn/OpcUa-Server](https://github.com/NBFinn/OpcUa-Server). They do not compile or use this adapter.

## Structure

```text
Adapter/
  OpcUaAdapter.cs                 Direct OPC UA connection
  Abstractions/                  IOpcUaAdapter and IOpcUaSessionManager
  Configuration/                 Adapter and connection settings
  Connections/                   Session manager and connection state
  Events/                        Connection and value messages
  Values/                        Raw values and cache
README.md                        Integration guide
```

All source files use `OpcUA_Server.Adapter`. Subfolders organize responsibilities; they do not introduce separate namespaces.

## Compatibility

The source files have been built for **.NET 6, 7, 8, 9 and 10** using C# 12 and the .NET 10 SDK. Connection testing against your own server is still required.

For .NET 6 and 7, current OPC UA and Microsoft dependencies emit target-framework support warnings. A successful build is not a support commitment from those package authors. These legacy targets should be evaluated against your deployment requirements.

.NET Framework 4.x, .NET Core 3.1 and .NET 5 have not been configured or tested for this source package. Compatibility with a target runtime does not imply compatibility with an older compiler.

## Add the adapter to your project

1. Clone this repository, or download and extract it from GitHub.
2. Copy `Adapter` into your C# project next to its `.csproj` file.
3. Enable implicit usings and nullable reference types, and use C# 12.
4. Add the NuGet packages below and build your project.

```powershell
git clone https://github.com/NBFinn/OpcUa-Adapter.git
dotnet add YourProject.csproj package OPCFoundation.NetStandard.Opc.Ua --version 1.5.378.176
dotnet add YourProject.csproj package Microsoft.Extensions.Logging.Abstractions --version 10.0.8
dotnet add YourProject.csproj package MassTransit --version 8.5.10
dotnet build YourProject.csproj
```

Example project settings:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <LangVersion>12.0</LangVersion>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

Replace `net8.0` with your chosen compatible target. SDK-style projects normally include C# files automatically. If your project sets `EnableDefaultCompileItems=false`, explicitly include them:

```xml
<ItemGroup>
  <Compile Include="Adapter\**\*.cs" />
</ItemGroup>
```

MassTransit is used by `OpcUaSessionManager` and its local mediator. It is not needed by the direct adapter class alone, but is required when compiling all twelve files. The local mediator does not require an external message broker.

## First connection and read

Start your OPC UA server, then try the following inside an async console application. Replace the address with the endpoint your server reports. The companion TestServer uses port `5844` and the path `/TestServerSimulator`.

```csharp
using OpcUA_Server.Adapter;

using var adapter = new OpcUaAdapter(
    "opc.tcp://localhost:5844/TestServerSimulator")
{
    Name = "TestServer",
    UseSecurity = false
};

try
{
    await adapter.ConnectAsync();
    var values = await adapter.ReadAllAsync();
    Console.WriteLine($"Connected: {adapter.IsConnected}");
    foreach (var value in values.Take(5))
        Console.WriteLine($"{value.NodeId}: {value.Value} (Good: {value.IsGood})");
}
finally
{
    await adapter.Disconnect();
}
```

`UseSecurity=false` selects an unsecured endpoint for a local connection test. Set it to `true` for a secured endpoint and configure certificate trust on both peers. HTTP addresses such as `http://localhost:6084` are REST API addresses, not OPC UA endpoints.

## Read and write individual nodes

After `ConnectAsync()` succeeds:

```csharp
string nodeId = "YOUR_COMPLETE_NODE_ID";
var current = await adapter.ReadAsync(nodeId);
Console.WriteLine(current.Value);

// Only for a writable Int16 node:
await adapter.WriteAsync(nodeId, (short)42);
```

Copy a complete NodeId from an OPC UA client or from `ReadAllAsync()`. Match the C# value type to the node: `(short)42` for Int16, `42` for Int32, `42f` for Float, `true` for Boolean and `"Text"` for String. Check write permissions before writing.

The adapter writes directly through OPC UA. Automatic switching to Manual is a feature of the companion web dashboard, not of the adapter. Cyclic or Scenario simulation can overwrite a directly written value; use Static or Manual for manual tests.

## Monitor value changes

After connecting, subscribe to selected nodes:

```csharp
adapter.ValueChanged += (changedNodeId, value) =>
    Console.WriteLine($"{changedNodeId}: {value.Value}");

await adapter.StartMonitoringAsync(new[] { nodeId }, publishingInterval: 1000);
Console.ReadLine(); // Keep the console application running
```

The interval is the requested publishing interval in milliseconds. `StartMonitoringAllAsync(1000)` monitors all browsed variables; start with a small node list for larger systems. The concrete class also provides `ReadManyAsync(...)` and `ReadAllAsync()`.

Callbacks run on OPC UA processing threads. Dispatch WPF or WinForms UI changes onto the UI thread. On shutdown, await `Disconnect()` before disposing the adapter. The `using` statement in the first example handles disposal.

## Cache and connection state

`adapter.RawValues` exposes `RawValueCache`, keyed by server name and NodeId. `GetValues()` reads cached values; `ValueUpdated` reports updates. Reading the cache does not perform a new server read.

`OpcUaRawValue` includes the NodeId, value, data type, source/server timestamps, status code and `IsGood`. Check the status before relying on a value.

`StatusChanged`, `ConnectionLost` and `ConnectionRestored` report connection changes. `ConnectionState` and `IsConnected` expose the current status. The direct class alone does not provide the session manager's complete reconnect orchestration.

## Manage multiple servers

Test a direct connection first. Use `OpcUaSessionManager` when you need configuration-driven connections, retry handling, monitoring and reconnect management for multiple servers.

1. Create one `OpcUaAdapterConfiguration` per server.
2. Set `Name` and `EndpointUrl`; optionally set `FallbackEndpointUrl`.
3. Set `ConnectionOptions.GetAll=false` and provide `NodeIds` for a selected node list. With `true`, all browsed variables are read and monitored.
4. Provide a shared `RawValueCache`, `ILoggerFactory`, `ILogger<OpcUaSessionManager>` and a MassTransit `IMediator`.
5. Await `Start(cancellationToken)` and await `Stop()` during shutdown.

The manager constructor accepts configurations as `IEnumerable<OpcUaAdapterConfiguration>`. In an application using dependency injection, register logging, the local mediator, configuration instances, cache and manager so that all constructor parameters can be resolved. A direct constructor call can supply the same dependencies explicitly.

| Setting | Default | Purpose |
| --- | --- | --- |
| ConnectionAttempts | 5 | Connection attempts |
| RetryDelay | 1 second | Delay between attempts |
| InitialReconnectDelay | 1 second | Initial reconnect delay |
| MaximumReconnectDelay | 30 seconds | Maximum reconnect delay |
| GetAll | true | All variables instead of the configured list |
| SubscriptionIntervalMilliseconds | 1000 | Requested publishing interval |

The manager publishes `OpcUaSessionEstablished`, `OpcUaRawValueChanged` and `OpcUaConnectionChanged` through the mediator. Add consumers if your application needs to handle those messages.

## Authentication and certificates

Set `UserName` and `Password` on the direct adapter if the server supports username authentication. With no username, it connects anonymously. The companion TestServer currently uses anonymous access; username/password authentication is not configured.

The adapter's default configuration stores certificates beside the executable under `OpcUA_Server/AdapterPki`, with `own`, `trusted`, `issuers` and `rejected` directories. Assign a custom OPC UA `ApplicationConfiguration` to `adapter.Configuration` before connecting if needed.

The direct class defaults `AcceptUntrustedCertificates` to `false`. The included session manager currently sets it to `true`, accepting unknown peer certificates; this does not bypass all certificate validation errors. Before use outside local tests, adjust that manager setting and establish the intended certificate trust explicitly.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Adapter types cannot be found | Copy the source folder into the project and check compile inclusion |
| Missing IMediator or logging types | Install and restore the required NuGet packages |
| Connection fails | Check the OPC UA address, path, port and running server; compare with another OPC UA client |
| BadCertificateUntrusted | Check both peers' trust stores; the simulator may regenerate its server certificate on restart |
| BadUserAccessDenied | Check server authentication support and node permissions |
| BadNodeIdUnknown | Use the complete NodeId from the connected server |
| No value-change events | Confirm monitoring started, the application remains running and values actually change |
| A written value changes back | Check simulation modes and other writing clients |

The adapter remains independent of the companion server. Validate its behavior in your target application and against your actual OPC UA server before depending on it.
