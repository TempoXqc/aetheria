using Aetheria.Server;
using Aetheria.Server.Persistence;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Net;

int port = ParsePort(args);

// Load content from the data/ folder next to the binary, falling back to built-in defaults.
string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
GameData gameData = GameData.LoadFromDirectoryOrDefault(dataDir);

// Durable state (accounts, banks, characters, names) — a JSON file today, Postgres later.
string statePath = ParseStatePath(args) ?? Path.Combine(AppContext.BaseDirectory, "state", "aetheria-state.json");
var store = new JsonFilePersistenceStore(statePath);
Console.WriteLine($"State file: {statePath}");

using var transport = new UdpServerTransport();
transport.Start(port);

var server = new GameServer(transport, gameData, Console.WriteLine, store);

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
    $"Aetheria server v{SimulationConstants.GameVersion} (protocol v{SimulationConstants.ProtocolVersion}) " +
    $"listening on UDP {port} at {SimulationConstants.TickRate} Hz.");
Console.WriteLine("Press Ctrl+C to stop.");

var loop = new FixedStepLoop(SimulationConstants.TickRate, dt =>
{
    server.ProcessNetwork();
    server.Tick(dt);
});

loop.Run(cts.Token);

Console.WriteLine("Server stopped.");
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
