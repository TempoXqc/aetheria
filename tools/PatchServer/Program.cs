using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------------------------------
// Aetheria Patch Server — the update pipe between the game's owner and the players.
//
//   PUBLISH a build into a channel (run on the owner's PC):
//     dotnet run --project tools/PatchServer -- publish <dossierDuBuild> <prod|staging> <version> [notes]
//
//   SERVE the published channels over HTTP (owner's PC, keep running):
//     dotnet run --project tools/PatchServer
//
// The launcher on every player's PC compares the channel MANIFEST (file hashes) with what is
// installed and downloads ONLY the files that changed — no more re-sending whole builds.
// ---------------------------------------------------------------------------------------------

// Accents in the launcher's log window: emit UTF-8 whatever the console codepage is.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

const int Port = 27080;
string root = FindRepoRootOrBase();
string buildsDir = Path.Combine(root, "builds");

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

if (args.Length >= 1 && args[0].Equals("publish", StringComparison.OrdinalIgnoreCase))
{
    return Publish(args);
}

// ------------------------------------------------------------------------- serve mode
Directory.CreateDirectory(buildsDir);
WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
WebApplication app = builder.Build();
app.Urls.Add($"http://0.0.0.0:{Port}");

// LIVE FRIENDS for the launcher: merge the realm's durable state (friends, last-seen)
// with its presence sidecar (who is online right now).
app.MapGet("/friends/{channel}", (string channel, HttpRequest request) =>
{
    string account = request.Query["account"].ToString().Trim().ToLowerInvariant();
    string baseName = Safe(channel) == "staging" ? "aetheria-tts" : "aetheria-prod";
    string stateFile = Path.Combine(root, "state", baseName + ".json");
    string presenceFile = stateFile + ".presence.json";
    var friends = new List<object>();
    string serverLabel = Safe(channel) == "staging" ? "PTR" : "Zul'jin";

    try
    {
        // Who is online right now (name → level), from the presence sidecar.
        var online = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(presenceFile))
        {
            using JsonDocument presence = JsonDocument.Parse(File.ReadAllText(presenceFile));
            if (presence.RootElement.TryGetProperty("players", out JsonElement players))
            {
                foreach (JsonElement pl in players.EnumerateArray())
                {
                    online[pl.GetProperty("name").GetString() ?? ""] = pl.GetProperty("level").GetInt32();
                }
            }
        }

        if (account.Length > 0 && File.Exists(stateFile))
        {
            using JsonDocument state = JsonDocument.Parse(File.ReadAllText(stateFile));
            JsonElement accounts = state.RootElement.GetProperty("Accounts");
            JsonElement names = state.RootElement.GetProperty("Names");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (accounts.TryGetProperty(account, out JsonElement me) &&
                me.TryGetProperty("Friends", out JsonElement list))
            {
                foreach (JsonElement friendKey in list.EnumerateArray())
                {
                    string key = friendKey.GetString() ?? "";
                    if (key.Length == 0) { continue; }

                    string display = key;
                    long lastSeen = 0;
                    if (names.TryGetProperty(key, out JsonElement ownerKey) &&
                        accounts.TryGetProperty(ownerKey.GetString() ?? "", out JsonElement owner) &&
                        owner.TryGetProperty("Characters", out JsonElement chars) &&
                        chars.TryGetProperty(key, out JsonElement rec))
                    {
                        if (rec.TryGetProperty("Name", out JsonElement dn)) { display = dn.GetString() ?? key; }
                        if (rec.TryGetProperty("LastSeenUnix", out JsonElement seen)) { lastSeen = seen.GetInt64(); }
                    }

                    bool isOn = online.TryGetValue(display, out int level);
                    friends.Add(new
                    {
                        name = display,
                        online = isOn,
                        level = isOn ? level : 0,
                        minutesSinceSeen = lastSeen > 0 ? (long)Math.Max(0, (now - lastSeen) / 60) : -1,
                        server = serverLabel,
                    });
                }
            }
        }
    }
    catch (Exception) { /* half-written files: answer with what we have */ }

    return Results.Json(new { friends });
});

// The channel manifest: version + per-file hashes. The launcher diffs against this.
app.MapGet("/manifest/{channel}", (string channel) =>
{
    string path = Path.Combine(buildsDir, Safe(channel), "manifest.json");
    return File.Exists(path)
        ? Results.Content(File.ReadAllText(path), "application/json")
        : Results.NotFound();
});

// One game file, by manifest path.
app.MapGet("/files/{channel}/{**filePath}", (string channel, string filePath) =>
{
    string channelDir = Path.GetFullPath(Path.Combine(buildsDir, Safe(channel)));
    string full = Path.GetFullPath(Path.Combine(channelDir, filePath));
    if (!full.StartsWith(channelDir, StringComparison.Ordinal) || !File.Exists(full))
    {
        return Results.NotFound();
    }

    return Results.File(full, "application/octet-stream");
});

// Patch notes shown in the launcher (notes.txt beside the tool; free-form text).
app.MapGet("/news", () =>
{
    string path = Path.Combine(root, "notes-launcher.txt");
    return Results.Content(File.Exists(path)
        ? File.ReadAllText(path, Encoding.UTF8)
        : "Bienvenue sur Aetheria !", "text/plain; charset=utf-8");
});

Console.WriteLine();
Console.WriteLine($"  Serveur de patchs Aetheria — http://0.0.0.0:{Port}");
foreach (string dir in Directory.Exists(buildsDir) ? Directory.GetDirectories(buildsDir) : Array.Empty<string>())
{
    string manifest = Path.Combine(dir, "manifest.json");
    if (File.Exists(manifest))
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
        Console.WriteLine($"  Canal « {Path.GetFileName(dir)} » : version {doc.RootElement.GetProperty("version").GetString()}, " +
                          $"{doc.RootElement.GetProperty("files").GetArrayLength()} fichiers.");
    }
}

Console.WriteLine("  (Ctrl+C pour arrêter — les launchers ne pourront plus vérifier les mises à jour.)");
Console.WriteLine();

// Open the TCP port on the router so friends' launchers reach us from the internet.
_ = Task.Run(() => Aetheria.Server.Net.UpnpPortOpener.TryOpenAsync(Port, Console.WriteLine, "TCP"));

try
{
    app.Run();
}
catch (IOException e) when (e.InnerException is System.Net.Sockets.SocketException
{
    SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse
})
{
    Console.WriteLine($"ERREUR : le port {Port} est déjà utilisé — un autre serveur de patchs tourne encore.");
    Console.WriteLine("Ferme l'ancien (ou redémarre le Launcher-Serveur, qui le fait tout seul).");
    return 1;
}

return 0;

// -------------------------------------------------------------------------- publish

int Publish(string[] a)
{
    if (a.Length < 4)
    {
        Console.WriteLine("Usage : publish <dossierDuBuild> <prod|staging> <version> [notes]");
        return 1;
    }

    string source = Path.GetFullPath(a[1]);
    string channel = Safe(a[2].ToLowerInvariant());
    string version = a[3];
    string notes = a.Length > 4 ? string.Join(' ', a.Skip(4)) : "";

    if (!Directory.Exists(source))
    {
        Console.WriteLine($"Dossier introuvable : {source}");
        return 1;
    }

    string target = Path.Combine(buildsDir, channel);
    Directory.CreateDirectory(target);

    // Copy the build in and hash every file for the manifest.
    var files = new List<object>();
    int copied = 0;
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(source, file).Replace('\\', '/');
        if (rel.Contains("DoNotShip", StringComparison.OrdinalIgnoreCase)) { continue; }

        string dest = Path.Combine(target, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
        copied++;

        using FileStream stream = File.OpenRead(dest);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        files.Add(new { path = rel, sha256 = hash, size = new FileInfo(dest).Length });
    }

    var manifest = new
    {
        version,
        channel,
        publishedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC",
        notes,
        files,
    };

    File.WriteAllText(Path.Combine(target, "manifest.json"),
        JsonSerializer.Serialize(manifest, jsonOptions), new UTF8Encoding(false));

    Console.WriteLine($"Canal « {channel} » publié : version {version}, {copied} fichiers.");
    Console.WriteLine("Les launchers verront la mise à jour à leur prochaine vérification.");
    return 0;
}

static string Safe(string channel)
    => channel.Replace("..", "").Replace('/', '_').Replace('\\', '_');

static string FindRepoRootOrBase()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
