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
// It checks the patch server for the selected CHANNEL (prod = jouable, staging = TTS/test),
// turns the PLAY button into METTRE À JOUR when the manifest differs, downloads ONLY the files
// whose hash changed, then launches the game — passing the saved account so the game logs in
// straight to the character screen. Login in-game still works without the launcher.
// ---------------------------------------------------------------------------------------------

string baseDir = AppContext.BaseDirectory;
string configPath = Path.Combine(BaseDataDir(), "launcher.json");
string htmlPath = FindAsset("launcher.html");
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
object updateLock = new();

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
WebApplication app = builder.Build();
app.Urls.Add("http://localhost:5180");

app.MapGet("/", () => Results.Content(File.ReadAllText(htmlPath, Encoding.UTF8), "text/html; charset=utf-8"));

// Everything the page needs: config + the up-to-date status of both channels.
app.MapGet("/api/state", async () =>
{
    JsonNode config = LoadConfig();
    var channels = new JsonObject();
    channels["prod"] = await ChannelState(config, "prod");
    channels["staging"] = await ChannelState(config, "staging");

    var result = new JsonObject
    {
        ["host"] = config["host"]!.GetValue<string>(),
        ["account"] = config["account"]!.GetValue<string>(),
        ["hasSecret"] = !string.IsNullOrEmpty(config["secret"]!.GetValue<string>()),
        ["channels"] = channels,
    };
    return Results.Content(result.ToJsonString(jsonOptions), "application/json; charset=utf-8");
});

// Patch notes relayed from the patch server.
app.MapGet("/api/news", async () =>
{
    JsonNode config = LoadConfig();
    try
    {
        string news = await http.GetStringAsync($"http://{config["host"]!.GetValue<string>()}/news");
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
    if (body?["host"] is not null) { config["host"] = body["host"]!.GetValue<string>().Trim(); }
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
    string host = config["host"]!.GetValue<string>();

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

    int downloaded = 0;
    long bytes = 0;
    lock (updateLock) { /* one update at a time */ }
    foreach (JsonNode? entry in remote["files"]!.AsArray())
    {
        string rel = entry!["path"]!.GetValue<string>();
        string expected = entry["sha256"]!.GetValue<string>();
        string local = Path.GetFullPath(Path.Combine(installDir, rel));
        if (!local.StartsWith(Path.GetFullPath(installDir), StringComparison.Ordinal))
        {
            continue; // path traversal: refuse
        }

        if (File.Exists(local) && HashOf(local) == expected)
        {
            continue; // already up to date
        }

        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        byte[] data = await http.GetByteArrayAsync($"http://{host}/files/{channel}/{rel}");
        await File.WriteAllBytesAsync(local, data);
        downloaded++;
        bytes += data.Length;
    }

    File.WriteAllText(Path.Combine(installDir, ".manifest.json"), remote.ToJsonString(jsonOptions));
    return Results.Ok(new
    {
        updated = downloaded,
        megabytes = Math.Round(bytes / 1048576.0, 1),
        version = remote["version"]!.GetValue<string>(),
    });
});

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

Console.WriteLine();
Console.WriteLine("  Launcher Aetheria — http://localhost:5180");
Console.WriteLine();
OpenBrowser("http://localhost:5180");
app.Run();

// --------------------------------------------------------------------------- helpers

async Task<JsonNode> ChannelState(JsonNode config, string channel)
{
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
            $"http://{config["host"]!.GetValue<string>()}/manifest/{channel}");
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
