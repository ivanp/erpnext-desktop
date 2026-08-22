using Serpy.Core.Contracts;
using Serpy.Core.Coordination;

namespace Serpy.Core.Tests.Coordination;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerпyTest-{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_dir, ApplianceState.FileName);

    private StateStore MakeStore() => new(StatePath);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Read_MissingFile_ReturnsDefaultNotBuilt()
    {
        var store = MakeStore();
        var state = store.Read();
        Assert.Equal(ReadinessState.NotBuilt, state.Readiness);
        Assert.Equal(HealthState.Stopped, state.Health);
    }

    [Fact]
    public void Write_ThenRead_Roundtrips()
    {
        var store = MakeStore();
        var original = new ApplianceState
        {
            Readiness = ReadinessState.Initialized,
            Health = HealthState.Running,
            QemuPid = 1234,
            LoopbackUrl = "http://127.0.0.1:18080",
        };
        store.Write(original);

        var loaded = store.Read();
        Assert.Equal(ReadinessState.Initialized, loaded.Readiness);
        Assert.Equal(HealthState.Running, loaded.Health);
        Assert.Equal(1234, loaded.QemuPid);
        Assert.Equal("http://127.0.0.1:18080", loaded.LoopbackUrl);
    }

    [Fact]
    public void Write_ThenRead_PreservesRecoverySourceImageIdentity()
    {
        var store = MakeStore();
        store.Write(new ApplianceState
        {
            RecoveryJournal = new RecoveryJournal
            {
                TargetImagePath = "C:\\replacement.qcow2",
                SourceImageSha256 = "AABBCC",
            },
        });

        var journal = Assert.IsType<RecoveryJournal>(store.Read().RecoveryJournal);
        Assert.Equal("AABBCC", journal.SourceImageSha256);
    }

    [Fact]
    public void Read_CorruptJson_ReturnsNotBuilt_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not valid json {{{{");
        var store = MakeStore();
        var state = store.Read(); // must not throw
        Assert.Equal(ReadinessState.NotBuilt, state.Readiness);
    }

    [Fact]
    public void Read_WrongSchemaVersion_ReturnsNotBuilt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath,
            """{"schemaVersion":999,"readiness":"Initialized","health":"Running"}""");
        var store = MakeStore();
        var state = store.Read();
        Assert.Equal(ReadinessState.NotBuilt, state.Readiness);
    }

    [Fact]
    public void Mutate_AppliesChangeAtomically()
    {
        var store = MakeStore();
        store.Write(new ApplianceState { Generation = 0 });

        store.Mutate(s =>
        {
            s.Generation = 42;
            s.Readiness = ReadinessState.Built;
        });

        var loaded = store.Read();
        Assert.Equal(42, loaded.Generation);
        Assert.Equal(ReadinessState.Built, loaded.Readiness);
    }
}
