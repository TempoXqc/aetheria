using System.Text.Json;

namespace Aetheria.Server.Persistence;

/// <summary>
/// Where durable state lives. The game code only ever talks to this interface; swapping the JSON
/// file store for Postgres later is a new implementation, not a refactor. Load once at boot, Save
/// whenever the server flushes (periodic + on disconnect).
/// </summary>
public interface IPersistenceStore
{
    ServerState Load();

    void Save(ServerState state);
}

/// <summary>Volatile store for tests and for running without persistence.</summary>
public sealed class InMemoryPersistenceStore : IPersistenceStore
{
    private ServerState _state = new();

    public ServerState Load() => _state;

    public void Save(ServerState state) => _state = state;
}

/// <summary>
/// A single-file JSON store with atomic writes: serialize to a temp file, then move over the real
/// one, so a crash mid-write can never corrupt the previous good state.
/// </summary>
public sealed class JsonFilePersistenceStore : IPersistenceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public JsonFilePersistenceStore(string path)
    {
        _path = path;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public ServerState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ServerState();
            }

            return JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_path), Options) ?? new ServerState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt/unreadable state file should not stop the server from booting; it starts
            // fresh and the operator still has the on-disk file to inspect.
            return new ServerState();
        }
    }

    public void Save(ServerState state)
    {
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
