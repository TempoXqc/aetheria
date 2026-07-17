using System.Diagnostics;
using System.Globalization;
using Aetheria.Client.TestHarness;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;

var options = HarnessOptions.Parse(args);

using var transport = new UdpClientTransport();
var client = new GameClient(transport);
client.Connect(options.Host, options.Port, options.Name);

Console.WriteLine(
    $"[{options.Name}] connecting to {options.Host}:{options.Port} " +
    $"(moving {options.Direction}, {options.Seconds:0.#}s)...");

var clock = Stopwatch.StartNew();
double lastStatusAt = 0;
double lastPingAt = 0;
int maxVisible = 0;
bool sawOther = false;

while (clock.Elapsed.TotalSeconds < options.Seconds)
{
    client.Pump();

    if (client.WasRejected)
    {
        Console.WriteLine($"[{options.Name}] REJECTED: {client.RejectReason}");
        break;
    }

    if (client.EntityId is int myId)
    {
        client.SendInput(options.Direction);

        maxVisible = System.Math.Max(maxVisible, client.Visible.Count);
        foreach (var e in client.Visible)
        {
            if (e.Id != myId)
            {
                sawOther = true;
            }
        }
    }

    double now = clock.Elapsed.TotalSeconds;

    if (now - lastPingAt >= 1.0 && client.EntityId is not null)
    {
        client.SendPing();
        lastPingAt = now;
    }

    if (now - lastStatusAt >= 1.0)
    {
        PrintStatus(client, options.Name);
        lastStatusAt = now;
    }

    Thread.Sleep(1000 / SimulationConstants.TickRate);
}

client.SendDisconnect();

bool connected = client.EntityId is not null;
Console.WriteLine(
    $"SUMMARY name={options.Name} connected={connected} " +
    $"entity={(client.EntityId?.ToString() ?? "none")} " +
    $"maxVisible={maxVisible} sawOther={sawOther} lastRttMs={client.LastRttMs}");

return connected ? 0 : 1;

static void PrintStatus(GameClient client, string name)
{
    string self = client.TryGetSelf(out var s) ? s.Position.ToString() : "(unknown)";
    var otherIds = new List<int>();
    foreach (var e in client.Visible)
    {
        if (e.Id != client.EntityId)
        {
            otherIds.Add(e.Id);
        }
    }

    Console.WriteLine(
        $"[{name}] tick={client.LastTick} self={self} visible={client.Visible.Count} " +
        $"others=[{string.Join(",", otherIds)}] rtt={client.LastRttMs}ms");
}

internal sealed record HarnessOptions(string Host, int Port, string Name, double Seconds, Vec2 Direction)
{
    public static HarnessOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = SimulationConstants.DefaultPort;
        string name = "tester";
        double seconds = 8;
        float dirX = 0;
        float dirY = 0;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port" when int.TryParse(args[i + 1], out int p): port = p; break;
                case "--name": name = args[i + 1]; break;
                case "--seconds" when double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s): seconds = s; break;
                case "--dirx" when float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x): dirX = x; break;
                case "--diry" when float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y): dirY = y; break;
                default: break;
            }
        }

        return new HarnessOptions(host, port, name, seconds, new Vec2(dirX, dirY));
    }
}
