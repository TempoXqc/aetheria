using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ---------------------------------------------------------------------------------------------
// Aetheria Launcher — what players double-click.
//
//   Launcher.bat  →  this backend + a WoW-style page in the browser.
//
// It checks the patch server for the selected CHANNEL (prod = Zul'jin, staging = PTR),
// turns the PLAY button into METTRE À JOUR when the manifest differs, downloads ONLY the files
// whose hash changed, then launches the game — passing the saved account so the game logs in
// straight to the character screen. Login in-game still works without the launcher.
// ---------------------------------------------------------------------------------------------

// Two personalities, two windows:
//   Launcher.bat          → PLAYER mode (default): channels, updates, play. No hosting, ever.
//   Launcher-Serveur.bat  → HOST mode (--host): servers + the git→build→publish pipeline.
bool hostMode = args.Contains("--host");
int uiPort = hostMode ? 5181 : 5180;

string baseDir = AppContext.BaseDirectory;
string configPath = Path.Combine(BaseDataDir(), "launcher.json");

// Players NEVER type an address: the official patch address ships in launcher.txt (committed
// with the game). The launcher prefers a LOCAL patch server when one answers (the host's PC),
// and falls back to the official address everywhere else. Re-probed continuously: starting the
// launcher BEFORE the patch server must not lock it onto the public route (hairpin trap).
string? confirmedLocalHost = null;
string htmlPath = FindAsset("launcher.html");
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

// DOWNLOADS get their own client with NO timeout: game files fetched across the internet
// (a friend's PC) can take minutes — the 8s status-probe timeout was killing installs
// mid-file and the page only saw "Unexpected end of JSON input".
var download = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

// Live progress of the running update, polled by the page while it downloads.
int progDone = 0, progTotal = 0;
long progBytes = 0;
string progFile = "";
bool progActive = false;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
object updateLock = new();

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
WebApplication app = builder.Build();
app.Urls.Add($"http://localhost:{uiPort}");

app.MapGet("/", () => Results.Content(
    File.ReadAllText(htmlPath, Encoding.UTF8).Replace("__MODE__", hostMode ? "host" : "player"),
    "text/html; charset=utf-8"));

// Everything the page needs: config + the up-to-date status of both channels.
app.MapGet("/api/state", async () =>
{
    string patchHost = await PatchHostAsync();
    JsonNode config = LoadConfig();
    var channels = new JsonObject();
    channels["prod"] = await ChannelState(config, "prod");
    channels["staging"] = await ChannelState(config, "staging");

    var result = new JsonObject
    {
        ["host"] = patchHost,
        ["account"] = config["account"]!.GetValue<string>(),
        ["hasSecret"] = !string.IsNullOrEmpty(config["secret"]!.GetValue<string>()),
        ["channels"] = channels,
    };
    return Results.Content(result.ToJsonString(jsonOptions), "application/json; charset=utf-8");
});

// LIVE FRIENDS relayed from the patch server (uses the saved account name).
app.MapGet("/api/friends", async (HttpRequest request) =>
{
    string channel = SafeChannel(request.Query["channel"].ToString());
    JsonNode config = LoadConfig();
    string account = config["account"]!.GetValue<string>();
    if (string.IsNullOrWhiteSpace(account))
    {
        return Results.Content("{\"friends\":[]}", "application/json");
    }

    try
    {
        string host = await PatchHostAsync();
        string body = await http.GetStringAsync(
            $"http://{host}/friends/{channel}?account={Uri.EscapeDataString(account.Trim())}");
        return Results.Content(body, "application/json");
    }
    catch (Exception)
    {
        return Results.Content("{\"friends\":[]}", "application/json");
    }
});

// Patch notes relayed from the patch server.
app.MapGet("/api/news", async () =>
{
    string patchHost = await PatchHostAsync();
    try
    {
        string news = await http.GetStringAsync($"http://{patchHost}/news");
        return Results.Content(news, "text/plain; charset=utf-8");
    }
    catch (Exception)
    {
        return Results.Content("(serveur de mises à jour injoignable)", "text/plain; charset=utf-8");
    }
});

app.MapPost("/api/config", async (HttpRequest request) =>
{
    JsonNode? body = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync());
    JsonNode config = LoadConfig();
    if (body?["account"] is not null) { config["account"] = body["account"]!.GetValue<string>().Trim(); }
    if (body?["secret"] is not null && body["secret"]!.GetValue<string>().Length > 0)
    {
        config["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body["secret"]!.GetValue<string>()));
    }

    SaveConfig(config);
    return Results.Ok(new { saved = true });
});

// Download every file whose hash differs from the remote manifest. Serialized: one at a time.
app.MapPost("/api/update", async (HttpRequest request) =>
{
    string channel = SafeChannel(request.Query["channel"].ToString());
    JsonNode config = LoadConfig();
    string host = await PatchHostAsync();

    JsonNode? remote;
    try
    {
        remote = JsonNode.Parse(await http.GetStringAsync($"http://{host}/manifest/{channel}"));
    }
    catch (Exception e)
    {
        return Results.BadRequest(new { error = "Serveur de mises à jour injoignable : " + e.Message });
    }

    if (remote is null)
    {
        return Results.BadRequest(new { error = "Manifeste illisible." });
    }

    string installDir = Path.Combine(BaseDataDir(), "game", channel);
    Directory.CreateDirectory(installDir);

    // The GAME must be CLOSED: Windows refuses to overwrite the files of a running
    // process, and the download died mid-file with a cryptic sharing violation.
    if (GameIsRunningFrom(installDir))
    {
        return Results.BadRequest(new
        {
            error = "Le jeu est encore ouvert — ferme Aetheria, puis relance la mise à jour.",
        });
    }

    lock (updateLock) { /* one update at a time */ }

    // PASS 1 — the shopping list: hash every local file, keep only what really changed.
    var needed = new List<(string rel, string local)>();
    foreach (JsonNode? entry in remote["files"]!.AsArray())
    {
        string rel = entry!["path"]!.GetValue<string>();
        string expected = entry["sha256"]!.GetValue<string>();
        string local = Path.GetFullPath(Path.Combine(installDir, rel));
        if (!local.StartsWith(Path.GetFullPath(installDir), StringComparison.Ordinal))
        {
            continue; // path traversal: refuse
        }

        if (!File.Exists(local) || HashOf(local) != expected)
        {
            needed.Add((rel, local));
        }
    }

    // PASS 2 — download each needed file, STREAMED to disk (no timeout, 3 tries each).
    // An interrupted run resumes for free: finished files pass the hash check next time.
    int downloaded = 0;
    long bytes = 0;
    progActive = true;
    progDone = 0;
    progTotal = needed.Count;
    progBytes = 0;
    try
    {
        foreach ((string rel, string local) in needed)
        {
            progFile = rel;
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using HttpResponseMessage resp = await download.GetAsync(
                        $"http://{host}/files/{channel}/{rel}",
                        HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await using (FileStream fs = File.Create(local))
                    {
                        await resp.Content.CopyToAsync(fs);
                    }

                    break;
                }
                catch (Exception) when (attempt < 3)
                {
                    await Task.Delay(1200); // hiccup: breathe, retry the same file
                }
            }

            downloaded++;
            bytes += new FileInfo(local).Length;
            progDone = downloaded;
            progBytes = bytes;
        }
    }
    catch (Exception e)
    {
        string hint = e is IOException && e.Message.Contains("being used by another process")
            ? "Le jeu est encore ouvert — ferme Aetheria, puis reclique : la mise à jour reprendra où elle en était."
            : $"Téléchargement interrompu ({progFile}) : {e.Message} — reclique, il reprendra où il en était.";
        return Results.BadRequest(new { error = hint });
    }
    finally
    {
        progActive = false;
    }

    // PRUNE: delete local files the manifest no longer lists (renamed/removed content — e.g.
    // the old servers.txt). The install dir is fully launcher-managed, so this is safe.
    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".manifest.json" };
    foreach (JsonNode? entry in remote["files"]!.AsArray())
    {
        keep.Add(entry!["path"]!.GetValue<string>().Replace('\\', '/'));
    }

    int pruned = 0;
    foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(installDir, file).Replace('\\', '/');
        if (!keep.Contains(rel))
        {
            try { File.Delete(file); pruned++; } catch (IOException) { /* in use: next time */ }
        }
    }

    File.WriteAllText(Path.Combine(installDir, ".manifest.json"), remote.ToJsonString(jsonOptions));
    return Results.Ok(new
    {
        updated = downloaded,
        pruned,
        megabytes = Math.Round(bytes / 1048576.0, 1),
        version = remote["version"]!.GetValue<string>(),
    });
});

// Live download progress (polled by the page every second while an update runs).
app.MapGet("/api/progress", () => Results.Json(new
{
    active = progActive,
    done = progDone,
    total = progTotal,
    megabytes = Math.Round(progBytes / 1048576.0, 1),
    file = progFile,
}));

// Launch the installed game, with auto-login when an account is saved.
app.MapPost("/api/play", (HttpRequest request) =>
{
    string channel = SafeChannel(request.Query["channel"].ToString());
    string installDir = Path.Combine(BaseDataDir(), "game", channel);
    string? exe = FindGameExe(installDir);
    if (exe is null)
    {
        return Results.BadRequest(new { error = "Jeu non installé pour ce canal — fais d'abord la mise à jour." });
    }

    JsonNode config = LoadConfig();
    string account = config["account"]!.GetValue<string>();
    string secretB64 = config["secret"]!.GetValue<string>();
    string arguments = "";
    if (!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(secretB64))
    {
        string secret = Encoding.UTF8.GetString(Convert.FromBase64String(secretB64));
        arguments = $"--account \"{account}\" --secret \"{secret}\" --autologin";
    }

    Process.Start(new ProcessStartInfo(exe, arguments)
    {
        WorkingDirectory = installDir,
        UseShellExecute = true,
    });
    return Results.Ok(new { launched = true });
});

// ---------------------------------------------------------------- HOST MODE
// When the launcher runs from the game's repo (the owner's PC), it also drives the whole
// pipeline: git watch → one-click pull + headless Unity build + publish — and the servers.
if (hostMode)
{
    Aetheria.Launcher.HostMode.Detect();
    if (!Aetheria.Launcher.HostMode.IsHost)
    {
        Console.WriteLine("  Launcher-Serveur : à lancer depuis le dépôt du jeu (dossier avec .git).");
    }
    else
    {
        // One double-click does the whole hosting day: servers up + build/publish when needed.
        Aetheria.Launcher.HostMode.AutoPilot();
    }
}

app.MapGet("/api/host", async () =>
    Results.Content((await Aetheria.Launcher.HostMode.State()).ToJsonString(jsonOptions),
        "application/json; charset=utf-8"));

app.MapPost("/api/host/update", async (HttpRequest request) =>
{
    JsonNode? body = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync());
    var channels = new List<string>();
    if (body?["channels"] is JsonArray list)
    {
        foreach (JsonNode? c in list)
        {
            string name = c?.GetValue<string>() ?? "";
            if (name is "prod" or "staging") { channels.Add(name); }
        }
    }

    if (channels.Count == 0) { channels.Add("staging"); }
    bool started = Aetheria.Launcher.HostMode.StartUpdateJob(channels.ToArray());
    return started ? Results.Ok(new { started = true }) : Results.BadRequest(new { error = "Déjà en cours." });
});

app.MapPost("/api/host/sharezip", async () =>
{
    string? zip = await Aetheria.Launcher.HostMode.CreateShareZip();
    if (zip is null)
    {
        return Results.BadRequest(new { error = "Création du ZIP impossible (voir le journal)." });
    }

    // Open the folder with the ZIP selected, so "send it to a friend" is one drag away.
    if (OperatingSystem.IsWindows())
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception)
        {
            // Purely a convenience — the path is shown in the UI either way.
        }
    }

    return Results.Ok(new { path = zip });
});

app.MapPost("/api/host/server", (HttpRequest request) =>
{
    string name = request.Query["name"].ToString();
    string action = request.Query["action"].ToString();
    string result = Aetheria.Launcher.HostMode.ServerAction(name, action);
    return Results.Ok(new { result });
});

AppDomain.CurrentDomain.ProcessExit += (_, _) => Aetheria.Launcher.HostMode.StopAll();

Console.WriteLine();
Console.WriteLine(hostMode
    ? $"  Launcher SERVEUR Aetheria — http://localhost:{uiPort}"
    : $"  Launcher Aetheria — http://localhost:{uiPort}");
Console.WriteLine();
OpenBrowser($"http://localhost:{uiPort}");
app.Run();

// --------------------------------------------------------------------------- helpers

async Task<JsonNode> ChannelState(JsonNode config, string channel)
{
    string patchHost = await PatchHostAsync();
    string installDir = Path.Combine(BaseDataDir(), "game", channel);
    string localManifestPath = Path.Combine(installDir, ".manifest.json");
    string localVersion = "—";
    if (File.Exists(localManifestPath))
    {
        try
        {
            localVersion = JsonNode.Parse(File.ReadAllText(localManifestPath))?["version"]?.GetValue<string>() ?? "—";
        }
        catch (JsonException) { /* corrupt: treated as not installed */ }
    }

    string remoteVersion = "";
    string notes = "";
    bool reachable = false; // the patch server answered (even "nothing published yet")
    bool published = false; // this channel has a build to offer
    try
    {
        HttpResponseMessage response = await http.GetAsync(
            $"http://{patchHost}/manifest/{channel}");
        reachable = true;
        if (response.IsSuccessStatusCode)
        {
            JsonNode? remote = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            remoteVersion = remote?["version"]?.GetValue<string>() ?? "";
            notes = remote?["notes"]?.GetValue<string>() ?? "";
            published = remoteVersion.Length > 0;
        }

        // 404 = the server is up but the channel is EMPTY (no build published yet).
    }
    catch (Exception)
    {
        reachable = false; // truly offline
    }

    bool installed = localVersion != "—";
    return new JsonObject
    {
        ["installed"] = installed,
        ["localVersion"] = localVersion,
        ["remoteVersion"] = remoteVersion,
        ["reachable"] = reachable,
        ["published"] = published,
        ["upToDate"] = published && installed && localVersion == remoteVersion,
        ["notes"] = notes,
    };
}

JsonNode LoadConfig()
{
    try
    {
        if (File.Exists(configPath))
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(configPath));
            if (node is not null)
            {
                node["host"] ??= "127.0.0.1:27080";
                node["account"] ??= "";
                node["secret"] ??= "";
                return node;
            }
        }
    }
    catch (JsonException) { /* corrupt config: rebuild */ }

    // First run: launcher.txt beside the launcher may pre-point at the owner's patch server.
    string seedHost = "127.0.0.1:27080";
    string seed = Path.Combine(baseDir, "launcher.txt");
    if (File.Exists(seed))
    {
        string line = File.ReadAllLines(seed).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? seedHost;
        seedHost = line;
    }

    return new JsonObject { ["host"] = seedHost, ["account"] = "", ["secret"] = "" };
}

void SaveConfig(JsonNode config)
{
    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    File.WriteAllText(configPath, config.ToJsonString(jsonOptions), new UTF8Encoding(false));
}

async Task<string> PatchHostAsync()
{
    // 1) A patch server on THIS machine always wins (the host plays on localhost, no hairpin).
    //    Once seen locally, stick to it; until then, keep re-probing at every call — the patch
    //    server may simply not be up yet.
    if (confirmedLocalHost != null)
    {
        return confirmedLocalHost;
    }

    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using HttpResponseMessage answer = await probe.GetAsync("http://127.0.0.1:27080/news");
        confirmedLocalHost = "127.0.0.1:27080";
        return confirmedLocalHost;
    }
    catch (Exception) { /* not the host's PC (or servers not started yet) */ }

    // 2) The official address committed with the game: beside the exe, else at the repo root.
    foreach (string candidate in new[]
             {
                 Path.Combine(AppContext.BaseDirectory, "launcher.txt"),
                 Path.Combine(RepoRootOrCwd(), "launcher.txt"),
             })
    {
        try
        {
            if (File.Exists(candidate))
            {
                string? line = File.ReadAllLines(candidate)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (line != null) { return line; }
            }
        }
        catch (IOException) { /* try the next spot */ }
    }

    return "127.0.0.1:27080"; // last resort
}

static string RepoRootOrCwd()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
    {
        dir = dir.Parent;
    }

    if (dir == null)
    {
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

// Any process running FROM the install dir (the game) blocks file overwrites on Windows.
bool GameIsRunningFrom(string installDir)
{
    string full = Path.GetFullPath(installDir);
    foreach (Process proc in Process.GetProcesses())
    {
        try
        {
            string? path = proc.MainModule?.FileName;
            if (path is not null &&
                Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // System/elevated processes refuse inspection — they are not our game.
        }
    }

    return false;
}

string BaseDataDir()
{
    // Everything the launcher installs lives next to it: portable, easy to delete.
    return Path.Combine(baseDir, "aetheria-launcher-data");
}

string FindAsset(string name)
{
    // Beside the binary in a published launcher; in the project folder when run from the repo.
    string beside = Path.Combine(baseDir, name);
    if (File.Exists(beside)) { return beside; }

    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        string candidate = Path.Combine(dir.FullName, "tools", "Launcher", name);
        if (File.Exists(candidate)) { return candidate; }
        dir = dir.Parent;
    }

    return beside;
}

static string SafeChannel(string channel)
    => channel == "staging" ? "staging" : "prod";

static string HashOf(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string? FindGameExe(string installDir)
{
    if (!Directory.Exists(installDir)) { return null; }

    return Directory.EnumerateFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly)
        .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase));
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // App window (no tabs/address bar) when Edge is around; classic browser otherwise.
            try
            {
                Process.Start(new ProcessStartInfo("msedge", $"--app={url}") { UseShellExecute = true });
            }
            catch (Exception)
            {
                Process.Start(new ProcessStartInfo("cmd", "/c start " + url) { CreateNoWindow = true });
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
    }
    catch (Exception) { /* the console prints the URL anyway */ }
}
