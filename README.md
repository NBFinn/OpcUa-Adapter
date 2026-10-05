# OPC-UA-Adapter – einbinden und verwenden

Dieses Repository enthält zwölf C#-Quelldateien und diese Anleitung. Es ist ein Quellcodepaket, kein eigenständig startbares Projekt und keine fertige DLL. Der Server und sein Dashboard verwenden den Adapter nicht.

## Inhalt

```text
Adapter/
  OpcUaAdapter.cs                 Direkte OPC-UA-Verbindung
  Abstractions/                  IOpcUaAdapter, IOpcUaSessionManager
  Configuration/                 Adapter- und Verbindungsoptionen
  Connections/                   SessionManager und ConnectionState
  Events/                        Ereignisse für Verbindungen und Werte
  Values/                        OpcUaRawValue und RawValueCache
README.md                        Anleitung im Hauptverzeichnis
```

Alle Quelldateien verwenden den Namespace `OpcUA_Server.Adapter`. Die Unterordner ordnen die Dateien nach Aufgabe; sie erzeugen keine zusätzlichen Namespaces.

## In ein eigenes Projekt einbinden

1. Das Repository klonen oder über GitHub als ZIP herunterladen und entpacken.
2. Den enthaltenen Ordner `Adapter` in dein C#-Projekt kopieren, neben dessen Projektdatei.
3. Ein Ziel von .NET 6 bis .NET 10 verwenden. `ImplicitUsings` und `Nullable` aktivieren sowie `LangVersion` auf `12.0` setzen. Für die geprüften Builds wurde das .NET-10-SDK verwendet.
4. Die folgenden NuGet-Pakete installieren. Die Versionen entsprechen der verwendeten Ausgangsversion:

```powershell
dotnet add DeinProjekt.csproj package OPCFoundation.NetStandard.Opc.Ua --version 1.5.378.176
dotnet add DeinProjekt.csproj package Microsoft.Extensions.Logging.Abstractions --version 10.0.8
dotnet add DeinProjekt.csproj package MassTransit --version 8.5.10
dotnet build DeinProjekt.csproj
```

Die folgenden Einstellungen im Zielprojekt setzen (Beispiel .NET 8):

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <LangVersion>12.0</LangVersion>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

Bei einem normalen SDK-Projekt werden die C#-Dateien automatisch eingebunden. Wenn dein Projekt `EnableDefaultCompileItems=false` setzt, zusätzlich aufnehmen:

```xml
<ItemGroup>
  <Compile Include="Adapter\**\*.cs" />
</ItemGroup>
```

MassTransit wird für den `OpcUaSessionManager` und dessen lokalen Mediator verwendet; für die direkte Adapterklasse allein ist es nicht nötig. Wenn du alle zwölf Dateien übernimmst, ist das Paket für den Build erforderlich. Einen externen Message Broker braucht der lokale Mediator nicht.

## Erst verbinden und lesen

In einer Konsolenanwendung folgenden Ablauf ausprobieren. Die Adresse durch den tatsächlichen OPC-UA-Endpunkt ersetzen, den dein Server beim Start ausgibt. Für den enthaltenen TestServer lautet der Pfad `/TestServerSimulator`, Port `5844`.

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
    Console.WriteLine($"Verbunden: {adapter.IsConnected}");
    foreach (var value in values.Take(5))
        Console.WriteLine($"{value.NodeId}: {value.Value} (Good: {value.IsGood})");
}
finally
{
    await adapter.Disconnect();
}
```

`UseSecurity=false` wählt einen unverschlüsselten Endpunkt für den lokalen Funktionstest. Für einen verschlüsselten Endpunkt `UseSecurity=true` setzen und die Zertifikate auf beiden Seiten passend vertrauen. HTTP-Adressen wie `http://localhost:6084` gehören zur Verwaltungs-API und können nicht als Adapter-Endpunkt verwendet werden.

## Einzelne Node lesen oder schreiben

Nach erfolgreichem `ConnectAsync()`:

```csharp
string nodeId = "DEINE_VOLLSTAENDIGE_NODE_ID";
var current = await adapter.ReadAsync(nodeId);
Console.WriteLine(current.Value);

// Nur für eine beschreibbare Int16-Node:
await adapter.WriteAsync(nodeId, (short)42);
```

Die vollständige NodeId aus dem OPC-UA-Client oder aus `ReadAllAsync()` übernehmen. Datentypen bewusst setzen: `(short)42` für Int16, `42` für Int32, `42f` für Float, `true` für Boolean, `"Text"` für String. Vor dem Schreiben das Schreibrecht der Node prüfen.

Der Adapter schreibt direkt über OPC-UA. Die automatische Umschaltung auf Manual gehört zur Serververwaltung und findet beim direkten Adapter-Aufruf nicht statt. Bei Cyclic oder Scenario kann die Simulation einen geschriebenen Wert wieder ändern; für manuelle Tests vorher Static oder Manual wählen.

## Änderungen abonnieren

Nach dem Verbindungsaufbau ausgewählte Nodes überwachen:

```csharp
adapter.ValueChanged += (nodeId, value) =>
    Console.WriteLine($"{nodeId}: {value.Value}");

await adapter.StartMonitoringAsync(new[] { nodeId }, publishingInterval: 1000);
Console.ReadLine(); // Anwendung am Leben halten
```

Der Parameter gibt das gewünschte Veröffentlichungsintervall in Millisekunden an. `StartMonitoringAllAsync(1000)` überwacht alle beim Browsen gefundenen Variablen. Für eine größere Anlage zuerst wenige Nodes auswählen. Weitere Methoden der konkreten Klasse sind `ReadManyAsync(...)` und `ReadAllAsync()`.

Die Rückrufe kommen aus der OPC-UA-Verarbeitung. In WPF/WinForms UI-Änderungen über den Dispatcher beziehungsweise `Invoke` ausführen. Beim Beenden zuerst `Disconnect()` abwarten und dann den Adapter entsorgen. Ein `using` wie im Beispiel übernimmt das Entsorgen.

## Cache und Status

`adapter.RawValues` enthält den `RawValueCache`. Dieser speichert Werte nach Servername und NodeId. `GetValues()` liest den aktuellen Cache; `ValueUpdated` meldet Aktualisierungen. Ein Cache-Wert ist kein zusätzlicher Live-Read vom Server.

`OpcUaRawValue` liefert unter anderem `NodeId`, `Value`, `DataType`, Zeitstempel, `StatusCode` und `IsGood`. Bei einem schlechten Status nicht ungeprüft mit dem Wert weiterarbeiten.

`StatusChanged`, `ConnectionLost` und `ConnectionRestored` melden Verbindungsänderungen. `ConnectionState` und `IsConnected` geben den aktuellen Zustand an. Die direkte Klasse allein bietet nicht die vollständige Wiederverbindungssteuerung des SessionManagers.

## SessionManager für mehrere Server

Erst die direkte Verbindung testen; danach bei Bedarf `OpcUaSessionManager` verwenden. Er verwaltet mehrere konfigurierte Adapter, Wiederholungsversuche, Überwachung und Wiederverbindungen.

1. Pro Server eine `OpcUaAdapterConfiguration` anlegen.
2. `Name` und `EndpointUrl` setzen; optional `FallbackEndpointUrl` angeben.
3. Mit `ConnectionOptions.GetAll=false` gezielt `NodeIds` angeben. Bei `true` werden alle gefundenen Variablen gelesen und überwacht.
4. Einen gemeinsamen `RawValueCache`, `ILoggerFactory`, `ILogger<OpcUaSessionManager>` und einen MassTransit-`IMediator` bereitstellen. In einer Anwendung mit Dependency Injection die Logging-Dienste und den lokalen Mediator registrieren und dann den Manager auflösen.
5. `Start(cancellationToken)` abwarten; beim Herunterfahren `Stop()` abwarten.

Der Konstruktor nimmt die Konfigurationen als `IEnumerable<OpcUaAdapterConfiguration>` entgegen. Für eine direkte Erstellung kannst du die benötigten Instanzen explizit übergeben; für eine bestehende DI-Anwendung müssen genau diese Konstruktorparameter auflösbar sein.

| Option | Standard | Wirkung |
| --- | --- | --- |
| ConnectionAttempts | 5 | Verbindungsversuche |
| RetryDelay | 1 Sekunde | Abstand zwischen Versuchen |
| InitialReconnectDelay | 1 Sekunde | Anfangsabstand bei Wiederverbindung |
| MaximumReconnectDelay | 30 Sekunden | Obergrenze des Wiederverbindungsabstands |
| GetAll | true | Alle Variablen statt konfigurierte NodeIds |
| SubscriptionIntervalMilliseconds | 1000 | Gewünschtes Veröffentlichungsintervall |

Der Manager veröffentlicht `OpcUaSessionEstablished`, `OpcUaRawValueChanged` und `OpcUaConnectionChanged` über den Mediator. Eigene Consumer sind nur nötig, wenn deine Anwendung diese Meldungen verarbeiten soll.

## Anmeldung und Zertifikate

Direkte Klasse: `UserName` und `Password` setzen, wenn der Zielserver Benutzeranmeldung unterstützt. Ohne Benutzername wird anonym verbunden. Der mitgelieferte TestServer unterstützt aktuell anonymen Zugriff; der Benutzer aus seiner JSON-Datei ist nicht an die Anmeldung angebunden.

Die direkte Klasse erzeugt ihre Standard-Zertifikatablage neben der ausführbaren Anwendung unter `OpcUA_Server/AdapterPki`, mit `own`, `trusted`, `issuers` und `rejected`. Eine eigene `ApplicationConfiguration` kann über `adapter.Configuration` vor dem Verbinden zugewiesen werden.

`AcceptUntrustedCertificates` ist bei der direkten Klasse standardmäßig `false`. Der enthaltene SessionManager setzt es derzeit intern auf `true` und akzeptiert damit unbekannte Peer-Zertifikate; andere Zertifikatfehler werden damit nicht pauschal aufgehoben. Vor Verwendung außerhalb lokaler Tests diesen Punkt im Manager anpassen und das gewünschte Vertrauen ausdrücklich konfigurieren.

## Fehlersuche

| Beobachtung | Prüfen |
| --- | --- |
| Namespace / Klasse nicht gefunden | Entpackten Adapterordner in das Zielprojekt kopiert? Compile-Einbindung vorhanden? |
| IMediator oder Logging-Typ fehlt | NuGet-Pakete installiert und wiederhergestellt? |
| Verbindung scheitert | OPC-UA-Adresse, Pfad, Port und gestarteten Server prüfen; Verbindung zuerst mit einem OPC-UA-Client vergleichen |
| BadCertificateUntrusted | Trust Stores beider Seiten prüfen; Serverzertifikat kann sich bei einem Simulator-Neustart ändern |
| BadUserAccessDenied | Unterstützte Anmeldung und Schreibrechte der Node prüfen |
| BadNodeIdUnknown | Vollständige aktuelle NodeId vom verbundenen Server übernehmen |
| Keine Änderungsmeldungen | Monitoring gestartet, Anwendung noch aktiv und Node-Wert tatsächlich verändert? |
| Wert springt zurück | Cyclic-/Scenario-Modus oder andere schreibende Clients prüfen |

Der Adapter wurde ursprünglich mit den oben genannten Paketversionen gebaut. Die Kompatibilität mit deinem Zielprojekt und die Verbindung zu deinem Zielserver müssen dort geprüft werden. Das Serverprojekt kann weiterhin gestartet werden, ohne dieses Quellcodepaket zu entpacken.

## Versionskompatibilität

Die zwölf Adapterdateien wurden für **net6.0, net7.0, net8.0, net9.0 und net10.0** mit C# 12 gebaut. Die Unterschiede bei Abbruch und Dispose-Prüfung werden im Code berücksichtigt; C#-14-Syntax wurde durch gleichwertige ältere Syntax ersetzt. Der Adapter bleibt ein separates Quellcodepaket und wird weiterhin nicht in den Server eingebunden.

Für .NET 6/7 melden die aktuellen OPC-UA- und Microsoft-Paketabhängigkeiten Kompatibilitätswarnungen. Der erfolgreiche Build ist keine Zusage des Paketautors für diese Laufzeiten. Eine Verbindung mit einem realen Zielserver muss im eigenen Zielprojekt getestet werden. .NET Framework 4.x und .NET Core 3.1 / .NET 5 wurden für dieses Quellcodepaket nicht eingerichtet oder geprüft.