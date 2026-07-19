using Aetheria.Server;
using Aetheria.Server.Persistence;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Net;

// Accents in the launcher's log window: emit UTF-8 whatever the console codepage is.
try { System.Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (System.IO.IOException) { }

int port = ParsePort(args);

// Load content from the data/ folder next to the binary, falling back to built-in defaults.
string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
GameData gameData = GameData.LoadFromDirectoryOrDefault(dataDir);

// Durable state (accounts, banks, characters, names) — a JSON file today, Postgres later.
string statePath = ParseStatePath(args) ?? Path.Combine(AppContext.BaseDirectory, "state", "aetheria-state.json");
var store = new JsonFilePersistenceStore(statePath);
Console.WriteLine($"State file: {statePath}");

using var transport = new UdpServerTransport();
try
{
    transport.Start(port);
}
catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    Console.WriteLine($"ERREUR : le port UDP {port} est déjà utilisé — un autre serveur tourne encore.");
    Console.WriteLine("Ferme l'ancien serveur (ou redémarre le Launcher-Serveur, qui le fait tout seul).");
    return 1;
}

string serverName = ParseArg(args, "--name") ?? SimulationConstants.DefaultServerName;
int maxPlayers = int.TryParse(ParseArg(args, "--max-players"), out int cap) && cap > 0
    ? cap
    : SimulationConstants.DefaultMaxPlayers;

var server = new GameServer(transport, gameData, Console.WriteLine, store, serverName, maxPlayers);

Console.WriteLine(
    $"Content loaded: {gameData.Races.Count} races, {gameData.Classes.Count} classes, " +
    $"{gameData.Monsters.Count} monster types.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Let the loop unwind cleanly instead of hard-killing the process.
    cts.Cancel();
};

Console.WriteLine(
    $"Aetheria server « {server.ServerName} » v{SimulationConstants.GameVersion} " +
    $"(protocol v{SimulationConstants.ProtocolVersion}) listening on UDP {port} " +
    $"at {SimulationConstants.TickRate} Hz — capacity {server.MaxPlayers} players.");
Console.WriteLine("Press Ctrl+C to stop.");

// Open the port on the home router automatically (UPnP) and print the PUBLIC IP —
// so friends can join over the internet without touching the router. Best-effort.
_ = Task.Run(() => Aetheria.Server.Net.UpnpPortOpener.TryOpenAsync(port, Console.WriteLine));

var loop = new FixedStepLoop(SimulationConstants.TickRate, dt =>
{
    server.ProcessNetwork();
    server.Tick(dt);
});

loop.Run(cts.Token);

// LAST save on the way out: everyone reconnects exactly where they were, even across restarts.
server.SaveNow();
Console.WriteLine("State saved. Server stopped.");
return 0;

static int ParsePort(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if ((args[i] == "--port" || args[i] == "-p") &&
            int.TryParse(args[i + 1], out int parsed) &&
            parsed is > 0 and <= 65535)
        {
            return parsed;
        }
    }

    return SimulationConstants.DefaultPort;
}

static string? ParseStatePath(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--state")
        {
            return args[i + 1];
        }
    }

    return null;
}

static string? ParseArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}
