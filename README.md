# OPC UA Adapter

A direct OPC UA client source package for connection, browsing, reading, writing and subscriptions. It contains five C# files. There is no mediator, MassTransit, message bus, dependency injection container or background session manager. The companion [server source module](https://github.com/NBFinn/OpcUa-Server) is also compiled into your own application; neither repository contains a standalone project.

## Structure

```text
Adapter/
  OpcUaAdapter.cs               Direct OPC UA session and subscriptions
  Abstractions/IOpcUaAdapter.cs Public interface
  Connections/ConnectionState.cs
  Values/OpcUaRawValue.cs       Raw value, timestamps and quality
  Values/RawValueCache.cs       Latest values and ValueUpdated event
```

The namespace is OpcUA_Server.Adapter. This remains a source package, not a standalone app or a prebuilt DLL. Copy Adapter into your own project. The folder structure stays organized; there are no project references to the original gateway.

## Add to your project

Use .NET 8, 9 or 10 with C# 12, implicit usings and nullable reference types enabled. The only direct package dependency is the OPC Foundation client SDK:

```powershell
dotnet add YourProject.csproj package OPCFoundation.NetStandard.Opc.Ua.Client --version 1.5.378.176
```

The SDK brings its required transitive dependencies. You do not need MassTransit or Microsoft.Extensions.Logging.Abstractions as explicit dependencies of this adapter. Cleanup diagnostics use System.Diagnostics.Trace. The previous session-manager configuration, mediator messages, injected logger constructor argument and GetAll field have been removed.

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <LangVersion>12.0</LangVersion>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

For projects with EnableDefaultCompileItems=false, explicitly include Adapter/**/*.cs. The source was built and tested on .NET 8, 9 and 10; older targets are no longer part of the validated package.

## Connect, read and write

Start the application that embeds the server module first and use the endpoint returned by its StartAsync method. Port 5844 is only its preference: a busy port causes automatic selection of another port.

```csharp
using OpcUA_Server.Adapter;

using var adapter = new OpcUaAdapter("opc.tcp://localhost:5844/TestServerSimulator")
{
    Name = "TestClient",
    UseSecurity = false
};

try
{
    await adapter.ConnectAsync();
    var current = await adapter.ReadAsync("ns=3;s=Test.Counter");
    Console.WriteLine($"{current.Value}, good: {current.IsGood}");
    await adapter.WriteAsync("ns=3;s=Test.Counter", 42);
    var all = await adapter.ReadAllAsync();
}
finally
{
    await adapter.Disconnect();
}
```

Write a correctly typed C# value: bool, int, double or string for the four test nodes. A value such as "42" is a string, not an Int32. Bad write results throw ServiceResultException. Read results expose IsGood and StatusCode; check quality before using a value. ReadAllAsync browses custom variables, excluding namespace 0 system nodes.

## Monitor values

Register the callback before creating the subscription:

```csharp
adapter.ValueChanged += (nodeId, value) =>
    Console.WriteLine($"{nodeId}: {value.Value}");
await adapter.StartMonitoringAsync(new[] { "ns=3;s=Test.Counter" }, 1000);
```

Keep the adapter connected while monitoring. Subscription callbacks run on OPC UA worker threads; marshal UI changes to your UI thread and keep callbacks short. RawValues stores the latest values and exposes ValueUpdated. No messages are published through a mediator.

StartMonitoringAllAsync monitors all custom variables. ReadManyAsync reads a chosen list. ConnectionState and StatusChanged expose state transitions. ConnectionLost reports keepalive failures. There is no automatic retry/fallback service: your application decides when to call ConnectAsync again. Existing monitored node IDs are recreated on reconnect. ConnectionRestored fires after a successful connection.

## Security and lifecycle

UseSecurity=true selects a secure endpoint. UserName and Password can be set when the target server supports user authentication. The companion server module supports anonymous access only.

AcceptUntrustedCertificates defaults to false. For encrypted connections, configure mutual certificate trust rather than assuming that connecting to a server makes it trusted. Client stores live under %LOCALAPPDATA%/OpcUaAdapter/pki/net<version>, with own, trusted, issuers and rejected subfolders. A caller may provide an ApplicationConfiguration for its own security settings.

Await Disconnect in a finally block and then dispose the adapter. Do not block subscription callbacks with synchronous reconnect or shutdown calls; schedule lifecycle operations in your application. Dispose is synchronous.

## Validation

Direct tests against the raw server passed on .NET 8, 9 and 10: connection state, browsing four nodes, typed reads/writes, rejected invalid writes, subscription notifications, raw cache updates, disconnect and reconnect. The server tests also covered occupied preferred ports and concurrent versions. Encrypted/authenticated remote connections and GUI applications were not exercised by these tests.
