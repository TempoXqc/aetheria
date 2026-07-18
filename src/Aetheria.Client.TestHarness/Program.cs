using System.Diagnostics;
using System.Globalization;
using Aetheria.Shared.Client;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

var options = HarnessOptions.Parse(args);

using var transport = new UdpClientTransport();
var client = new GameClient(transport);
client.Connect(options.Host, options.Port, options.Name, options.RaceId, options.ClassId, options.Gender, options.AccountId, options.Secret);
bool deposited = false;

Console.WriteLine(
    $"[{options.Name}] connecting to {options.Host}:{options.Port} " +
    $"(race={options.RaceId} class={options.ClassId} " +
    $"{(options.Attack ? $"attacking with ability {options.AbilityId}" : $"moving {options.Direction}")}, " +
    $"{options.Seconds:0.#}s)...");

var clock = Stopwatch.StartNew();
double lastStatusAt = 0;
double lastPingAt = 0;
double lastRacialAt = 0;
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
        // Stash gold in the account bank once, so it survives permadeath.
        if (options.DepositGold > 0 && !deposited)
        {
            client.SendBank(BankOp.DepositGold, 0, options.DepositGold);
            deposited = true;
        }

        // Enter the requested instance once (solo, or as party leader).
        if (options.Instance != 0 && !client.InInstance && string.IsNullOrEmpty(client.LastInstanceMessage))
        {
            client.SendEnterInstance(options.Instance);
        }

        // Auto-accept any party invite (handy for scripted multi-client tests).
        if (client.PendingInviteFrom is not null)
        {
            client.SendPartyRespond(accept: true);
        }

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

        if (options.Loot)
        {
            int corpse = client.FindNearestCorpse();
            if (corpse >= 0)
            {
                client.SendLootCorpse(corpse); // server enforces range; harmless if too far
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

    if (options.Racial && client.EntityId is not null && now - lastRacialAt >= 1.0)
    {
        client.SendUseRacial(); // server enforces the racial's cooldown
        lastRacialAt = now;
    }

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
    $"entity={(client.EntityId?.ToString() ?? "none")} level={client.Level} xp={client.TotalXp} gold={client.Gold} bank={client.BankGold} " +
    $"maxVisible={maxVisible} sawOther={sawOther} combatSeen={client.CombatEventsSeen} " +
    $"killsByMe={client.KillsByMe} lastRttMs={client.LastRttMs}" +
    (string.IsNullOrEmpty(client.LastInstanceMessage) ? string.Empty : $" instanceMsg=\"{client.LastInstanceMessage}\""));

return connected ? 0 : 1;

static void PrintStatus(GameClient client, string name)
{
    string hp = "?";
    string res = "?";
    string self = "(unknown)";
    if (client.TryGetSelf(out var s))
    {
        hp = $"{s.Health}/{s.MaxHealth}";
        res = $"{s.Resource}/{s.MaxResource}";
        self = s.Position.ToString();
    }

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

    string xp = client.XpForNextLevel >= 0 ? $"{client.TotalXp}/{client.XpForNextLevel}" : $"{client.TotalXp}(max)";

    string zone = client.InInstance ? " zone=INSTANCE" : string.Empty;
    string party = client.PartySize > 0 ? $" party={client.PartySize}({client.PartyLeader})" : string.Empty;

    Console.WriteLine(
        $"[{name}] tick={client.LastTick} lvl={client.Level} xp={xp} gold={client.Gold} bank={client.BankGold} " +
        $"hp={hp} res={res} self={self} visible={client.Visible.Count} monsters={monsters}{zone}{party}{combat}");
}

internal sealed record HarnessOptions(
    string Host, int Port, string Name, double Seconds, Vec2 Direction,
    byte RaceId, byte ClassId, byte AbilityId, bool Attack, Gender Gender, bool Racial, bool Loot,
    string AccountId, int DepositGold, byte Instance, string Secret)
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
        var gender = Gender.Male;
        bool racial = false;
        bool loot = false;
        string account = "";
        int depositGold = 0;
        byte instance = 0;
        string secret = "";

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
                case "--gender" when HasNext(): gender = ParseGender(args[++i]); break;
                case "--account" when HasNext(): account = args[++i]; break;
                case "--deposit" when HasNext() && int.TryParse(args[i + 1], out int dg): depositGold = dg; i++; break;
                case "--instance" when HasNext() && byte.TryParse(args[i + 1], out byte inst): instance = inst; i++; break;
                case "--secret" when HasNext(): secret = args[++i]; break;
                case "--attack": attack = true; break;
                case "--racial": racial = true; break;
                case "--loot": loot = true; break;
                default: break;
            }
        }

        // Default the account id to the character name if not given, so each name has its own bank.
        if (string.IsNullOrWhiteSpace(account))
        {
            account = name;
        }

        if (string.IsNullOrEmpty(secret))
        {
            secret = account; // sensible default for scripted tests
        }

        return new HarnessOptions(host, port, name, seconds, new Vec2(dirX, dirY), race, cls, ability, attack, gender, racial, loot, account, depositGold, instance, secret);
    }

    private static Gender ParseGender(string value) => value.ToLowerInvariant() switch
    {
        "f" or "female" or "1" => Gender.Female,
        _ => Gender.Male,
    };
}
