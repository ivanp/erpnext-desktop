using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;

namespace Serpy.Core.Coordination;

/// <summary>
/// Atomic JSON state store: write-to-temp-then-rename, never corrupt the live file.
/// Uses source-generated serialization for Native-AOT compatibility.
/// All reads/writes are synchronous within the lifecycle lock.
/// </summary>
public sealed class StateStore
{
    private readonly string _path;

    public StateStore(string? path = null)
    {
        _path = path ?? Path.Combine(KnownPaths.SettingsDir, ApplianceState.FileName);
    }

    public ApplianceState Read()
    {
        if (!File.Exists(_path))
            return new ApplianceState();

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, ApplianceStateJsonContext.Default.ApplianceState);
            if (state is null || state.SchemaVersion != ApplianceState.CurrentSchemaVersion)
                return new ApplianceState { Readiness = ReadinessState.NotBuilt };
            return state;
        }
        catch
        {
            // Corrupt or unreadable state — treat as unbuilt, recoverable diagnostic logged by caller.
            return new ApplianceState { Readiness = ReadinessState.NotBuilt };
        }
    }

    /// <summary>
    /// Atomically write state: serialize to a sibling .tmp file, then rename over.
    /// </summary>
    public void Write(ApplianceState state)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(state, ApplianceStateJsonContext.Default.ApplianceState);
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Read → mutate via <paramref name="mutate"/> → write atomically.
    /// </summary>
    public ApplianceState Mutate(Action<ApplianceState> mutate)
    {
        var state = Read();
        mutate(state);
        Write(state);
        return state;
    }
}
