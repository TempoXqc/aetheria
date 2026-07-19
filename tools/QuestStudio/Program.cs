using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ---------------------------------------------------------------------------------------------
// Studio de Quêtes — a tiny local website for writing Aetheria quests WITHOUT touching code.
//
//   dotnet run --project tools/QuestStudio        (or double-click Studio-Quetes.bat)
//
// It reads the game's own data files (monsters, items, quests), serves a French editing UI at
// http://localhost:5178, validates quest chains, saves to src/Aetheria.Server/data/quests.json,
// and "Publier" commits + pushes that file to git. The game server picks the new quests up on
// its next restart, and CLIENTS receive them at login (QuestCatalog) — no rebuild anywhere.
// ---------------------------------------------------------------------------------------------

string repoRoot = FindRepoRoot();
string dataDir = Path.Combine(repoRoot, "src", "Aetheria.Server", "data");
string questsPath = Path.Combine(dataDir, "quests.json");
string htmlPath = Path.Combine(repoRoot, "tools", "QuestStudio", "studio.html");

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
WebApplication app = builder.Build();
app.Urls.Add("http://localhost:5178");

// The one-page UI.
app.MapGet("/", () => Results.Content(File.ReadAllText(htmlPath, Encoding.UTF8), "text/html; charset=utf-8"));

// Item icons — the same PNGs the game uses (Ultimate RPG Items pack).
string iconsDir = Path.Combine(repoRoot, "unity", "AetheriaClient", "Assets", "Resources", "Icons");
app.MapGet("/icons/{id:int}", (int id) =>
{
    string png = Path.Combine(iconsDir, "item_" + id + ".png");
    return File.Exists(png)
        ? Results.File(png, "image/png")
        : Results.NotFound();
});

// Everything the editor needs: monster IDs, item IDs, and the current quests.
app.MapGet("/api/data", () =>
{
    JsonNode? monsters = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "monsters.json")));
    JsonNode? items = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "items.json")));
    JsonNode? quests = File.Exists(questsPath)
        ? JsonNode.Parse(File.ReadAllText(questsPath))
        : new JsonArray();

    var slim = new JsonObject
    {
        ["monsters"] = new JsonArray(((JsonArray)monsters!).Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m!["id"]!.GetValue<int>(),
            ["name"] = m["name"]!.GetValue<string>(),
            ["level"] = m["level"]?.GetValue<int>() ?? 1,
        }).ToArray()),
        ["items"] = new JsonArray(((JsonArray)items!).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i!["id"]!.GetValue<int>(),
            ["name"] = i["name"]!.GetValue<string>(),
            ["type"] = i["type"]?.GetValue<int>() ?? 0,
            ["slot"] = i["slot"]?.GetValue<int>() ?? 0,
            ["attackBonus"] = i["attackBonus"]?.GetValue<int>() ?? 0,
            ["defenseBonus"] = i["defenseBonus"]?.GetValue<int>() ?? 0,
            ["healthBonus"] = i["healthBonus"]?.GetValue<int>() ?? 0,
            ["goldValue"] = i["goldValue"]?.GetValue<int>() ?? 0,
            ["stackable"] = i["stackable"]?.GetValue<bool>() ?? false,
            ["maxStack"] = i["maxStack"]?.GetValue<int>() ?? 1,
        }).ToArray()),
        ["quests"] = quests,
    };

    return Results.Content(slim.ToJsonString(jsonOptions), "application/json; charset=utf-8");
});

// Save the full quest list (validated) into the game's quests.json.
app.MapPost("/api/quests", async (HttpRequest request) =>
{
    string body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    JsonArray? quests;
    try
    {
        quests = JsonNode.Parse(body) as JsonArray;
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "JSON invalide." });
    }

    if (quests is null)
    {
        return Results.BadRequest(new { error = "Le corps doit être une liste de quêtes." });
    }

    List<string> problems = Validate(quests,
        LoadIds(Path.Combine(dataDir, "monsters.json")), LoadIds(Path.Combine(dataDir, "items.json")));
    if (problems.Count > 0)
    {
        return Results.BadRequest(new { error = string.Join("\n", problems) });
    }

    File.WriteAllText(questsPath, quests.ToJsonString(jsonOptions) + Environment.NewLine,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // no BOM: git-friendly diffs
    return Results.Ok(new { saved = quests.Count });
});

// Commit + push data/quests.json. The server owner does a git pull + restart to go live.
app.MapPost("/api/publish", async (HttpRequest request) =>
{
    string body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    string message = "Quêtes : mise à jour depuis le Studio";
    try
    {
        JsonNode? node = JsonNode.Parse(body);
        string? custom = node?["message"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(custom)) { message = custom.Trim(); }
    }
    catch (JsonException) { /* keep the default message */ }

    (int code, string output) = await Git(repoRoot, "add", Path.GetRelativePath(repoRoot, questsPath));
    if (code != 0) { return Results.BadRequest(new { error = "git add a échoué :\n" + output }); }

    (code, output) = await Git(repoRoot, "commit", "-m", message);
    if (code != 0)
    {
        bool nothing = output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("rien à valider", StringComparison.OrdinalIgnoreCase);
        return nothing
            ? Results.Ok(new { published = false, info = "Aucun changement à publier — tout est déjà à jour." })
            : Results.BadRequest(new { error = "git commit a échoué :\n" + output });
    }

    (code, output) = await Git(repoRoot, "push");
    return code != 0
        ? Results.BadRequest(new { error = "git push a échoué :\n" + output })
        : Results.Ok(new { published = true, info = "Publié ! Le serveur du jeu doit faire git pull puis redémarrer." });
});

Console.WriteLine();
Console.WriteLine("  Studio de Quêtes Aetheria — http://localhost:5178");
Console.WriteLine("  (Ctrl+C pour arrêter. Les quêtes vont dans " + Path.GetRelativePath(repoRoot, questsPath) + ")");
Console.WriteLine();
TryOpenBrowser("http://localhost:5178");
app.Run();

// ------------------------------------------------------------------------------- helpers

static string FindRepoRoot()
{
    // Walk up from where the tool runs until the .git folder appears.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
    {
        dir = dir.Parent;
    }

    if (dir == null)
    {
        // Fallback: the current working directory (dotnet run from the repo).
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
    }

    return dir?.FullName ?? throw new InvalidOperationException(
        "Impossible de trouver la racine du dépôt (dossier .git). Lance le Studio depuis le dépôt Aetheria.");
}

static HashSet<int> LoadIds(string path)
{
    var ids = new HashSet<int>();
    if (JsonNode.Parse(File.ReadAllText(path)) is JsonArray array)
    {
        foreach (JsonNode? node in array)
        {
            int? id = node?["id"]?.GetValue<int>();
            if (id.HasValue) { ids.Add(id.Value); }
        }
    }

    return ids;
}

static List<string> Validate(JsonArray quests, HashSet<int> monsterIds, HashSet<int> itemIds)
{
    var problems = new List<string>();
    var seen = new HashSet<int>();
    var all = new HashSet<int>();
    foreach (JsonNode? q in quests)
    {
        int? id = q?["id"]?.GetValue<int>();
        if (id.HasValue) { all.Add(id.Value); }
    }

    foreach (JsonNode? node in quests)
    {
        if (node is not JsonObject q) { problems.Add("Une entrée n'est pas un objet quête."); continue; }

        int id = q["id"]?.GetValue<int>() ?? 0;
        string label = "Quête " + id + (q["name"] != null ? " (« " + q["name"]!.GetValue<string>() + " »)" : "");

        if (id is < 1 or > 255) { problems.Add(label + " : l'id doit être entre 1 et 255."); }
        if (!seen.Add(id)) { problems.Add(label + " : id en double."); }
        if (string.IsNullOrWhiteSpace(q["name"]?.GetValue<string>())) { problems.Add(label + " : il faut un nom."); }

        int target = q["targetMonsterId"]?.GetValue<int>() ?? 0;
        if (!monsterIds.Contains(target)) { problems.Add(label + " : monstre cible inconnu (id " + target + ")."); }

        int kills = q["requiredKills"]?.GetValue<int>() ?? 0;
        if (kills is < 1 or > 1000) { problems.Add(label + " : le nombre à tuer doit être entre 1 et 1000."); }

        int rewardItem = q["rewardItemId"]?.GetValue<int>() ?? 0;
        if (rewardItem != 0 && !itemIds.Contains(rewardItem)) { problems.Add(label + " : objet de récompense inconnu (id " + rewardItem + ")."); }

        int next = q["nextQuestId"]?.GetValue<int>() ?? 0;
        if (next != 0 && !all.Contains(next)) { problems.Add(label + " : la quête suivante (id " + next + ") n'existe pas."); }
        if (next == id && id != 0) { problems.Add(label + " : une quête ne peut pas se suivre elle-même."); }

        if ((q["rewardXp"]?.GetValue<int>() ?? 0) < 0) { problems.Add(label + " : l'XP ne peut pas être négative."); }
        if ((q["rewardGold"]?.GetValue<int>() ?? 0) < 0) { problems.Add(label + " : l'or ne peut pas être négatif."); }
    }

    return problems;
}

static async Task<(int code, string output)> Git(string repoRoot, params string[] arguments)
{
    var psi = new ProcessStartInfo("git")
    {
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string a in arguments) { psi.ArgumentList.Add(a); }

    using var process = Process.Start(psi)!;
    string stdout = await process.StandardOutput.ReadToEndAsync();
    string stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, (stdout + "\n" + stderr).Trim());
}

static void TryOpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("cmd", "/c start " + url) { CreateNoWindow = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
    }
    catch
    {
        // No browser? The URL is printed in the console anyway.
    }
}
