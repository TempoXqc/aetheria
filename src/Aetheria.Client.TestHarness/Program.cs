using System.Diagnostics;
using System.Globalization;
using Aetheria.Client.TestHarness;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

var options = HarnessOptions.Parse(args);

using var transport = new UdpClientTransport();
var client = new GameClient(transport);
client.Connect(options.Host, options.Port, options.Name, options.RaceId, options.ClassId);

Console.WriteLine(
    $"[{options.Name}] connecting to {options.Host}:{options.Port} " +
    $"(race={options.RaceId} class={options.ClassId} " +
    $"{(options.Attack ? $"attacking with ability {options.AbilityId}" : $"moving {options.Direction}")}, " +
    $"{options.Seconds:0.#}s)...");

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
        Vec2 moveDir = options.Direction;

        if (options.Attack)
        {
            int target = client.FindNearestMonster();
            if (target >= 0 && client.TryGetSelf(out var self) && client.TryGetEntity(target, out var mob))
            {
                moveDir = (mob.Position - self.Position).Normalized(); // close in on the monster
                client.SendUseAbility(options.AbilityId, target);       // server enforces range/cooldown
            }
        }

        client.SendInput(moveDir);

        maxVisible = System.Math.Max(maxVisible, client.Visible.Count);
        foreach (var e in client.Visible)
        {
            if (e.Id != myId && e.Kind == EntityKind.Player)
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
    $"maxVisible={maxVisible} sawOther={sawOther} combatSeen={client.CombatEventsSeen} " +
    $"killsByMe={client.KillsByMe} lastRttMs={client.LastRttMs}");

return connected ? 0 : 1;

static void PrintStatus(GameClient client, string name)
{
    string hp = client.TryGetSelf(out var s) ? $"{s.Health}/{s.MaxHealth}" : "?";
    string self = client.TryGetSelf(out var s2) ? s2.Position.ToString() : "(unknown)";

    int monsters = 0;
    foreach (var e in client.Visible)
    {
        if (e.Kind == EntityKind.Monster)
        {
            monsters++;
        }
    }

    string combat = client.LastCombat is CombatEventMessage c
        ? $" lastHit={c.AttackerId}->{c.TargetId} dmg={c.Damage} rem={c.TargetRemainingHealth}{(c.TargetKilled ? " KILL" : "")}"
        : string.Empty;

    Console.WriteLine(
        $"[{name}] tick={client.LastTick} hp={hp} self={self} " +
        $"visible={client.Visible.Count} monsters={monsters}{combat}");
}

internal sealed record HarnessOptions(
    string Host, int Port, string Name, double Seconds, Vec2 Direction,
    byte RaceId, byte ClassId, byte AbilityId, bool Attack)
{
    public static HarnessOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = SimulationConstants.DefaultPort;
        string name = "tester";
        double seconds = 8;
        float dirX = 0;
        float dirY = 0;
        byte race = 1;
        byte cls = 1;
        byte ability = 1;
        bool attack = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool HasNext() => i + 1 < args.Length;

            switch (a)
            {
                case "--host" when HasNext(): host = args[++i]; break;
                case "--port" when HasNext() && int.TryParse(args[i + 1], out int p): port = p; i++; break;
                case "--name" when HasNext(): name = args[++i]; break;
                case "--seconds" when HasNext() && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s): seconds = s; i++; break;
                case "--dirx" when HasNext() && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x): dirX = x; i++; break;
                case "--diry" when HasNext() && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y): dirY = y; i++; break;
                case "--race" when HasNext() && byte.TryParse(args[i + 1], out byte r): race = r; i++; break;
                case "--class" when HasNext() && byte.TryParse(args[i + 1], out byte c): cls = c; i++; break;
                case "--ability" when HasNext() && byte.TryParse(args[i + 1], out byte ab): ability = ab; i++; break;
                case "--attack": attack = true; break;
                default: break;
            }
        }

        return new HarnessOptions(host, port, name, seconds, new Vec2(dirX, dirY), race, cls, ability, attack);
    }
}
