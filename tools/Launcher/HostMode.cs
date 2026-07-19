using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Aetheria.Launcher;

/// <summary>
/// HOST MODE — active only when the launcher runs from the game's repository on the owner's PC.
/// One window drives everything: watch the git remote for new commits, pull + build the Unity
/// client HEADLESSLY + publish to the patch channels on one click, and start/stop the game
/// servers (prod, TTS) and the patch server. Friends' launchers light up by themselves once a
/// publish lands.
/// </summary>
public static class HostMode
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Process> Servers = new();
    private static readonly StringBuilder JobLog = new();
    private static string _jobPhase = "idle"; // idle | pull | build | publish | done | error
    private static DateTime _lastFetch = DateTime.MinValue;
    private static int _behind = -1;
    private static bool _launcherOutdated;
    private static readonly string[] StagingOnly = { "staging" };

    public static string? RepoRoot { get; private set; }

    public static bool IsHost => RepoRoot is not null;

    public static void Detect()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (cwd != null && !Directory.Exists(Path.Combine(cwd.FullName, ".git")))
            {
                cwd = cwd.Parent;
            }

            dir = cwd;
        }

        if (dir != null && Directory.Exists(Path.Combine(dir.FullName, "unity", "AetheriaClient")))
        {
            RepoRoot = dir.FullName;
        }
    }

    /// <summary>
    /// AUTOPILOT — runs once when the Launcher-Serveur opens: starts the three servers, then
    /// builds & publishes automatically when the staging channel is missing, stale (version
    /// differs from the code), or the repo has new commits. Zero clicks for the usual day.
    /// </summary>
    public static void AutoPilot()
    {
        if (!IsHost)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Binaries FIRST (nothing runs yet: zero lock risk), then the servers — each
                // from its own copy, so later rebuilds never collide with them.
                if (await BuildHostBinaries())
                {
                    Log("── Pilote automatique : démarrage des serveurs…");
                    ServerAction("patch", "start");
                    ServerAction("prod", "start");
                    ServerAction("tts", "start");
                }

                await Git("fetch", "--quiet");
                _lastFetch = DateTime.UtcNow;
                _behind = int.TryParse((await Git("rev-list", "HEAD..origin/master", "--count")).output.Trim(),
                    out int n) ? n : 0;

                bool needBuild = _behind > 0;
                string reason = _behind > 0 ? _behind + " nouveau(x) commit(s)" : "";

                string manifestPath = Path.Combine(RepoRoot!, "builds", "staging", "manifest.json");
                string current = await GameVersion();
                if (!File.Exists(manifestPath))
                {
                    needBuild = true;
                    reason = "aucun build publié";
                }
                else
                {
                    try
                    {
                        string? published = System.Text.Json.Nodes.JsonNode
                            .Parse(await File.ReadAllTextAsync(manifestPath))?["version"]?.GetValue<string>();
                        if (published != current)
                        {
                            needBuild = true;
                            reason = $"build publié ({published}) ≠ code ({current})";
                        }
                    }
                    catch (Exception) { needBuild = true; reason = "manifeste illisible"; }
                }

                if (needBuild)
                {
                    Log($"── Pilote automatique : construction nécessaire ({reason}).");
                    StartUpdateJob(StagingOnly);
                }
                else
                {
                    Log("── Pilote automatique : build à jour, rien à construire.");
                }
            }
            catch (Exception e)
            {
                Log("Pilote automatique : " + e.Message);
            }
        });
    }

    // ------------------------------------------------------------------ state

    public static async Task<JsonObject> State()
    {
        if (!IsHost)
        {
            return new JsonObject { ["isHost"] = false };
        }

        // A light fetch at most every 2 minutes keeps the "new version available" fresh.
        if (DateTime.UtcNow - _lastFetch > TimeSpan.FromMinutes(2) && _jobPhase != "pull")
        {
            _lastFetch = DateTime.UtcNow;
            await Git("fetch", "--quiet");
            _behind = int.TryParse((await Git("rev-list", "HEAD..origin/master", "--count")).output.Trim(),
                out int n) ? n : -1;
        }

        var servers = new JsonObject();
        foreach (string name in new[] { "prod", "tts", "patch" })
        {
            lock (Gate)
            {
                servers[name] = Servers.TryGetValue(name, out Process? p) && p is { HasExited: false };
            }
        }

        string log;
        lock (Gate) { log = JobLog.ToString(); }
        return new JsonObject
        {
            ["isHost"] = true,
            ["behind"] = _behind,
            ["phase"] = _jobPhase,
            ["log"] = log.Length > 4000 ? log[^4000..] : log,
            ["servers"] = servers,
            ["unityFound"] = FindUnity() is not null,
            ["launcherOutdated"] = _launcherOutdated,
        };
    }

    // ------------------------------------------------- one-click update pipeline

    /// <summary>git pull → headless Unity build → publish to the chosen channels.</summary>
    public static bool StartUpdateJob(string[] channels)
    {
        lock (Gate)
        {
            if (_jobPhase is "pull" or "build" or "publish")
            {
                return false; // already running
            }

            _jobPhase = "pull";
            JobLog.Clear();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Log("── Récupération des derniers changements (git pull)…");
                string beforeHead = (await Git("rev-parse", "HEAD")).output.Trim();
                (int code, string output) = await Git("pull", "--rebase", "--autostash", "origin", "master");
                Log(output);
                if (code != 0) { Fail("git pull a échoué."); return; }

                // Did the pull change the LAUNCHER's own code? Then this running backend is
                // stale — flag it so the page tells the host to restart Launcher-Serveur.bat.
                string changed = (await Git("diff", "--name-only", beforeHead, "HEAD")).output;
                if (changed.Split('\n').Any(f =>
                    f.StartsWith("tools/Launcher/", StringComparison.Ordinal) &&
                    !f.EndsWith("launcher.html", StringComparison.Ordinal)))
                {
                    _launcherOutdated = true;
                    Log("⚠ Le launcher lui-même a changé : relance Launcher-Serveur.bat après cette mise à jour.");
                }

                SetPhase("build");
                string unity = FindUnity() ?? throw new InvalidOperationException(
                    "Unity introuvable (Unity Hub attendu dans Program Files).");
                string buildDir = Path.Combine(RepoRoot!, "builds-auto");
                string logFile = Path.Combine(RepoRoot!, "builds-auto-log.txt");
                Log("── Construction du jeu avec Unity (quelques minutes, sans fenêtre)…");
                Log("   " + unity);

                int buildCode = await Run(unity,
                    $"-quit -batchmode -nographics -projectPath \"{Path.Combine(RepoRoot!, "unity", "AetheriaClient")}\" " +
                    $"-executeMethod Aetheria.UnityClient.EditorTools.CommandLineBuild.Build " +
                    $"-buildPath \"{buildDir}\" -logFile \"{logFile}\"",
                    RepoRoot!, TimeSpan.FromMinutes(30));
                if (buildCode != 0)
                {
                    string tail = File.Exists(logFile) ? TailOf(File.ReadAllText(logFile), 1500) : "(pas de log)";
                    Fail("Le build Unity a échoué :\n" + tail);
                    return;
                }

                Log("── Build OK.");
                SetPhase("publish");

                string version = await GameVersion();
                foreach (string channel in channels)
                {
                    // The realm list ships INSIDE the build, PER CHANNEL: the Aetheria channel
                    // sees only Zul'jin, the PTR channel sees only PTR. Public IP for friends,
                    // LAN for the house, localhost for this PC.
                    await WriteServersFile(buildDir, channel);
                    Log($"── Publication sur le canal « {channel} » (version {version})…");
                    PublishChannel(buildDir, channel, version); // in-process: no compile, no locks
                }

                // Refresh the server binaries (the pull may have changed server code), then
                // restart ONLY the realm(s) whose channel was published so the server matches
                // the new client (protocol!). Seconds of downtime; the 5-second autosave plus
                // position persistence mean players reconnect exactly where they were.
                if (!await BuildHostBinaries()) { Fail("Compilation des serveurs échouée."); return; }
                foreach (string channel in channels)
                {
                    string realm = channel == "prod" ? "prod" : "tts";
                    if (IsServerRunning(realm))
                    {
                        Log($"── Redémarrage du serveur « {realm} » (nouvelle version)…");
                        ServerAction(realm, "stop");
                        await Task.Delay(1500);
                        ServerAction(realm, "start");
                    }
                }

                _behind = 0;
                Log("── Terminé ! Les launchers voient la nouvelle version.");
                SetPhase("done");
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        });
        return true;
    }

    /// <summary>
    /// The friend package: a ZIP with ONLY the player launcher (exe + page + official address).
    /// No repo, no server launcher, no game inside — the launcher downloads the game from the
    /// patch server on first run, so the zip never goes stale. Returns the zip path, or null.
    /// </summary>
    public static string? CreateShareZip()
    {
        try
        {
            string source = Path.Combine(RepoRoot!, "artifacts", "bin", "Launcher", "release");
            if (!File.Exists(Path.Combine(source, "Launcher.exe")) &&
                !File.Exists(Path.Combine(source, "Launcher.dll")))
            {
                Log("ZIP amis : binaires du launcher introuvables.");
                return null;
            }

            string stage = Path.Combine(RepoRoot!, "run", "partage-stage");
            if (Directory.Exists(stage)) { Directory.Delete(stage, recursive: true); }
            CopyDirectory(source, stage);

            File.Copy(Path.Combine(RepoRoot!, "tools", "Launcher", "launcher.html"),
                Path.Combine(stage, "launcher.html"), overwrite: true);
            string officialAddress = Path.Combine(RepoRoot!, "launcher.txt");
            if (File.Exists(officialAddress))
            {
                File.Copy(officialAddress, Path.Combine(stage, "launcher.txt"), overwrite: true);
            }

            File.WriteAllText(Path.Combine(stage, "LISEZMOI.txt"),
                "AETHERIA — Launcher\r\n" +
                "===================\r\n\r\n" +
                "1) Installe .NET 10 (une fois) : https://dotnet.microsoft.com/download\r\n" +
                "2) Double-clique Launcher.exe\r\n" +
                "3) Entre ton compte + mot de passe (créés au premier INSTALLER > JOUER)\r\n" +
                "4) Clique INSTALLER : le jeu se télécharge tout seul, puis JOUER.\r\n\r\n" +
                "Les mises à jour sont automatiques : quand le bouton dit\r\n" +
                "« Mettre à jour », un clic suffit.\r\n",
                new UTF8Encoding(false));

            string shareDir = Path.Combine(RepoRoot!, "partage");
            Directory.CreateDirectory(shareDir);
            string zip = Path.Combine(shareDir, "Aetheria-Amis.zip");
            if (File.Exists(zip)) { File.Delete(zip); }
            System.IO.Compression.ZipFile.CreateFromDirectory(stage, zip);
            Log("── ZIP pour amis créé : " + zip);
            return zip;
        }
        catch (Exception e)
        {
            Log("ZIP amis : " + e.Message);
            return null;
        }
    }

    /// <summary>Copy the build into builds/{channel} and write its manifest — pure file work,
    /// done IN THIS PROCESS so nothing recompiles while realms run.</summary>
    private static void PublishChannel(string buildDir, string channel, string version)
    {
        string target = Path.Combine(RepoRoot!, "builds", channel);
        Directory.CreateDirectory(target);

        var files = new JsonArray();
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(buildDir, file).Replace('\\', '/');
            if (rel.Contains("DoNotShip", StringComparison.OrdinalIgnoreCase)) { continue; }

            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            copied++;

            using FileStream stream = File.OpenRead(dest);
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            files.Add(new JsonObject
            {
                ["path"] = rel,
                ["sha256"] = hash,
                ["size"] = new FileInfo(dest).Length,
            });
        }

        var manifest = new JsonObject
        {
            ["version"] = version,
            ["channel"] = channel,
            ["publishedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC",
            ["notes"] = "Mise à jour automatique",
            ["files"] = files,
        };

        File.WriteAllText(Path.Combine(target, "manifest.json"),
            manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Log($"Canal « {channel} » publié : version {version}, {copied} fichiers.");
    }

    /// <summary>
    /// realms.txt for ONE channel: the Aetheria channel ships only Zul'jin, the PTR channel
    /// only PTR — each installed game sees exactly its own realm.
    /// </summary>
    private static async Task WriteServersFile(string buildDir, string channel)
    {
        string publicIp = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            publicIp = (await http.GetStringAsync("https://api.ipify.org")).Trim();
        }
        catch (Exception) { /* offline: LAN + localhost still work */ }

        string lanIp = "";
        try
        {
            using var probe = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            lanIp = (probe.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";
        }
        catch (Exception) { /* no network */ }

        static string Routes(string pub, string lan, int port)
        {
            var parts = new List<string>();
            if (pub.Length > 0) { parts.Add($"{pub}:{port}"); }
            if (lan.Length > 0) { parts.Add($"{lan}:{port}"); }
            parts.Add($"127.0.0.1:{port}");
            return string.Join("|", parts);
        }

        string content = channel == "prod"
            ? $"Zul'jin|{Routes(publicIp, lanIp, 27015)}\n"
            : $"PTR|{Routes(publicIp, lanIp, 27016)}\n";
        await File.WriteAllTextAsync(Path.Combine(buildDir, "realms.txt"), content);
        Log("── realms.txt généré (" + (publicIp.Length > 0 ? "IP publique " + publicIp : "sans IP publique") + ").");
    }

    private static async Task<string> GameVersion()
    {
        // The single source of truth: GameVersion in SimulationConstants.cs.
        try
        {
            string text = await File.ReadAllTextAsync(Path.Combine(
                RepoRoot!, "src", "Aetheria.Shared", "SimulationConstants.cs"));
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(text, "GameVersion = \"([^\"]+)\"");
            if (match.Success) { return match.Groups[1].Value; }
        }
        catch (IOException) { /* fall through */ }

        return DateTime.UtcNow.ToString("yyyy.MM.dd.HHmm");
    }

    // ------------------------------------------------------------- game servers

    public static bool IsServerRunning(string name)
    {
        lock (Gate)
        {
            return Servers.TryGetValue(name, out Process? p) && p is { HasExited: false };
        }
    }

    /// <summary>
    /// Compile the server binaries ONCE (game server + patch server). Realms run from COPIES
    /// (see ServerAction), so this never fights a running process over a locked DLL.
    /// </summary>
    private static async Task<bool> BuildHostBinaries()
    {
        Log("── Compilation des serveurs (une fois, avant démarrage)…");
        foreach (string project in new[] { "src/Aetheria.Server", "tools/PatchServer" })
        {
            (int code, string output) = await RunDotnet($"build -c Release {project}");
            if (code != 0)
            {
                Log(TailOf(output, 1500));
                Log($"ERREUR : compilation de {project} échouée.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Servers execute from a per-realm COPY of the build output — never from artifacts/ —
    /// so recompiling (updates!) NEVER hits "file locked by a running server".
    /// </summary>
    public static string ServerAction(string name, string action)
    {
        string stateDir = Path.Combine(RepoRoot!, "state");
        (string artifactDir, string dll, string arguments) = name switch
        {
            "prod" => (Path.Combine("artifacts", "bin", "Aetheria.Server", "release"), "Aetheria.Server.dll",
                $"--name \"Zul'jin\" --port 27015 --state \"{Path.Combine(stateDir, "aetheria-prod.json")}\""),
            "tts" => (Path.Combine("artifacts", "bin", "Aetheria.Server", "release"), "Aetheria.Server.dll",
                $"--name \"PTR\" --port 27016 --state \"{Path.Combine(stateDir, "aetheria-tts.json")}\""),
            "patch" => (Path.Combine("artifacts", "bin", "PatchServer", "release"), "PatchServer.dll", ""),
            _ => (string.Empty, string.Empty, string.Empty),
        };

        if (artifactDir.Length == 0)
        {
            return "serveur inconnu";
        }

        lock (Gate)
        {
            Servers.TryGetValue(name, out Process? existing);
            bool running = existing is { HasExited: false };

            if (action == "stop")
            {
                if (running)
                {
                    try { existing!.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                }

                Servers.Remove(name);
                try { File.Delete(Path.Combine(RepoRoot!, "run", name + ".pid")); } catch (IOException) { }
                return "arrêté";
            }

            if (running)
            {
                return "déjà en route";
            }

            string sourceDir = Path.Combine(RepoRoot!, artifactDir);
            if (!File.Exists(Path.Combine(sourceDir, dll)))
            {
                Log($"[{name}] binaires absents — compilation pas encore faite ?");
                return "binaires absents";
            }

            // One-time migration: the old prod state lived under artifacts/ — carry it over.
            Directory.CreateDirectory(stateDir);
            string prodState = Path.Combine(stateDir, "aetheria-prod.json");
            string legacyState = Path.Combine(sourceDir, "state", "aetheria-state.json");
            if (name == "prod" && !File.Exists(prodState) && File.Exists(legacyState))
            {
                File.Copy(legacyState, prodState);
                Log("[prod] anciennes sauvegardes migrées vers state/aetheria-prod.json.");
            }

            // ORPHAN GUARD: a previous launcher may have died without stopping its servers —
            // their ports stay taken and the fresh start crashes (10048). Each server leaves a
            // PID file; if that process is still alive, kill it before starting anew.
            string pidFile = Path.Combine(RepoRoot!, "run", name + ".pid");
            try
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int oldPid))
                {
                    Process orphan = Process.GetProcessById(oldPid);
                    if (!orphan.HasExited &&
                        orphan.ProcessName.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"[{name}] ancien serveur orphelin (pid {oldPid}) — arrêt avant redémarrage.");
                        orphan.Kill(entireProcessTree: true);
                        orphan.WaitForExit(3000);
                    }
                }
            }
            catch (ArgumentException) { /* pid no longer exists: nothing to do */ }
            catch (Exception e) { Log($"[{name}] garde anti-orphelin : {e.Message}"); }

            string runDir = Path.Combine(RepoRoot!, "run", name);
            CopyDirectory(sourceDir, runDir);

            var psi = new ProcessStartInfo("dotnet",
                ($"\"{Path.Combine(runDir, dll)}\" {arguments}").TrimEnd())
            {
                WorkingDirectory = RepoRoot!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process process = Process.Start(psi)!;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { Log($"[{name}] {e.Data}"); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { Log($"[{name}] {e.Data}"); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Servers[name] = process;
            try { File.WriteAllText(pidFile, process.Id.ToString()); } catch (IOException) { }
            return "démarré";
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            if (rel.StartsWith("state", StringComparison.OrdinalIgnoreCase))
            {
                continue; // never duplicate save files into run copies
            }

            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { File.Copy(file, dest, overwrite: true); }
            catch (IOException) { /* file busy from a lingering process: keep the old copy */ }
        }
    }

    public static void StopAll()
    {
        lock (Gate)
        {
            foreach (KeyValuePair<string, Process> entry in Servers)
            {
                try { if (!entry.Value.HasExited) { entry.Value.Kill(entireProcessTree: true); } }
                catch (Exception) { }

                try { File.Delete(Path.Combine(RepoRoot!, "run", entry.Key + ".pid")); }
                catch (IOException) { }
            }

            Servers.Clear();
        }
    }

    // ----------------------------------------------------------------- helpers

    private static string? FindUnity()
    {
        try
        {
            string versionFile = Path.Combine(RepoRoot!, "unity", "AetheriaClient",
                "ProjectSettings", "ProjectVersion.txt");
            string wanted = "";
            if (File.Exists(versionFile))
            {
                foreach (string line in File.ReadAllLines(versionFile))
                {
                    if (line.StartsWith("m_EditorVersion:"))
                    {
                        wanted = line.Split(':')[1].Trim();
                    }
                }
            }

            foreach (string hub in new[]
                     {
                         @"C:\Program Files\Unity\Hub\Editor",
                         @"C:\Program Files (x86)\Unity\Hub\Editor",
                     })
            {
                if (!Directory.Exists(hub)) { continue; }

                string exact = Path.Combine(hub, wanted, "Editor", "Unity.exe");
                if (wanted.Length > 0 && File.Exists(exact)) { return exact; }

                // Any installed editor beats none.
                string? any = Directory.GetDirectories(hub)
                    .Select(d => Path.Combine(d, "Editor", "Unity.exe"))
                    .FirstOrDefault(File.Exists);
                if (any != null) { return any; }
            }
        }
        catch (Exception) { /* not found */ }

        return null;
    }

    private static async Task<(int code, string output)> Git(params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in arguments) { psi.ArgumentList.Add(a); }

        using Process p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output.Trim());
    }

    private static async Task<(int code, string output)> RunDotnet(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = RepoRoot!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output.Trim());
    }

    private static async Task<int> Run(string exe, string arguments, string cwd, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        Task done = p.WaitForExitAsync();
        if (await Task.WhenAny(done, Task.Delay(timeout)) != done)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            return -1;
        }

        return p.ExitCode;
    }

    private static void Log(string line)
    {
        lock (Gate)
        {
            JobLog.AppendLine(line);
            if (JobLog.Length > 60000) { JobLog.Remove(0, JobLog.Length - 40000); }
        }
    }

    private static void SetPhase(string phase)
    {
        lock (Gate) { _jobPhase = phase; }
    }

    private static void Fail(string message)
    {
        Log("ERREUR : " + message);
        SetPhase("error");
    }

    private static string TailOf(string text, int chars)
        => text.Length <= chars ? text : text[^chars..];
}
