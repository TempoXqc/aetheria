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
                (int code, string output) = await Git("pull", "--rebase", "origin", "master");
                Log(output);
                if (code != 0) { Fail("git pull a échoué."); return; }

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
                    Log($"── Publication sur le canal « {channel} » (version {version})…");
                    (int pubCode, string pubOut) = await RunDotnet(
                        $"run -c Release --project tools{Path.DirectorySeparatorChar}PatchServer -- " +
                        $"publish \"{buildDir}\" {channel} {version} Mise à jour automatique");
                    Log(pubOut);
                    if (pubCode != 0) { Fail($"Publication {channel} échouée."); return; }
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

    public static string ServerAction(string name, string action)
    {
        (string project, string arguments) = name switch
        {
            "prod" => ("src/Aetheria.Server", "--name \"Zul'jin\" --port 27015"),
            "tts" => ("src/Aetheria.Server",
                "--name \"Zul'jin TTS\" --port 27016 --state state/aetheria-tts.json"),
            "patch" => ("tools/PatchServer", ""),
            _ => (string.Empty, string.Empty),
        };

        if (project.Length == 0)
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
                return "arrêté";
            }

            if (running)
            {
                return "déjà en route";
            }

            var psi = new ProcessStartInfo("dotnet",
                $"run -c Release --project {project} -- {arguments}".Replace("--  ", "-- ").TrimEnd())
            {
                WorkingDirectory = RepoRoot!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (arguments.Length == 0)
            {
                psi.Arguments = $"run -c Release --project {project}";
            }

            Process process = Process.Start(psi)!;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { Log($"[{name}] {e.Data}"); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { Log($"[{name}] {e.Data}"); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Servers[name] = process;
            return "démarré";
        }
    }

    public static void StopAll()
    {
        lock (Gate)
        {
            foreach (Process p in Servers.Values)
            {
                try { if (!p.HasExited) { p.Kill(entireProcessTree: true); } } catch (Exception) { }
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
