using System.Text.Json;

namespace Serpy.Core.Configuration;

/// <summary>
/// Atomic JSON store for host <see cref="SetupState"/>: write-to-temp-then-rename,
/// never corrupting the live file. Source-generated serialization keeps it
/// Native-AOT compatible.
///
/// Separate from the appliance lifecycle state store on purpose (see
/// <see cref="SetupState"/>); it holds only first-run setup markers.
/// </summary>
public sealed class SetupStateStore
{
    private readonly string _path;

    public SetupStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(KnownPaths.SettingsDir, SetupState.FileName);
    }

    /// <summary>
    /// Read current setup state. A missing, corrupt, or schema-mismatched file
    /// yields a fresh default (no restart pending) rather than throwing.
    /// </summary>
    public SetupState Read()
    {
        if (!File.Exists(_path))
            return new SetupState();

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, SetupStateJsonContext.Default.SetupState);
            if (state is null || state.SchemaVersion != SetupState.CurrentSchemaVersion)
                return new SetupState();
            return state;
        }
        catch
        {
            return new SetupState();
        }
    }

    /// <summary>Atomically write state via a sibling .tmp then rename over.</summary>
    public void Write(SetupState state)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(state, SetupStateJsonContext.Default.SetupState);
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Read → mutate → write atomically; returns the written state.</summary>
    public SetupState Mutate(Action<SetupState> mutate)
    {
        var state = Read();
        mutate(state);
        Write(state);
        return state;
    }
}
