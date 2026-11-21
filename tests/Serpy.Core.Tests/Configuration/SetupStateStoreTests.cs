using Serpy.Core.Configuration;
using Serpy.Core.Contracts;

namespace Serpy.Core.Tests.Configuration;

public sealed class SetupStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpySetupTest-{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_dir, SetupState.FileName);

    private SetupStateStore MakeStore() => new(StatePath);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Read_MissingFile_ReturnsDefaultNoRestartRequired()
    {
        var store = MakeStore();
        var state = store.Read();
        Assert.False(state.RestartRequired);
        Assert.Equal(SetupState.CurrentSchemaVersion, state.SchemaVersion);
    }

    [Fact]
    public void Write_ThenRead_RoundtripsRestartRequired()
    {
        var store = MakeStore();
        store.Write(new SetupState { RestartRequired = true });

        var reread = MakeStore().Read();
        Assert.True(reread.RestartRequired);
    }

    [Fact]
    public void Read_CorruptJson_ReturnsDefault_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ this is not valid json");

        var state = MakeStore().Read();
        Assert.False(state.RestartRequired);
    }

    [Fact]
    public void Read_WrongSchemaVersion_ReturnsDefault()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"schemaVersion\":999,\"restartRequired\":true}");

        var state = MakeStore().Read();
        // Schema mismatch is treated as fresh state, never a trusted restart flag.
        Assert.False(state.RestartRequired);
        Assert.Equal(SetupState.CurrentSchemaVersion, state.SchemaVersion);
    }

    [Fact]
    public void Mutate_SetThenClear_PersistsAcrossReads()
    {
        var store = MakeStore();

        store.Mutate(s => s.RestartRequired = true);
        Assert.True(MakeStore().Read().RestartRequired);

        // Simulates the post-reboot path clearing the marker once WHPX is confirmed live.
        store.Mutate(s => s.RestartRequired = false);
        Assert.False(MakeStore().Read().RestartRequired);
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        // The settings directory need not exist beforehand.
        Assert.False(Directory.Exists(_dir));
        MakeStore().Write(new SetupState { RestartRequired = true });
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void ASchema1File_LoadsAsSchema2WithDefaults_AndKeepsItsRestartFlag()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"schemaVersion\":1,\"restartRequired\":true}");

        var state = MakeStore().Read();

        Assert.Equal(2, state.SchemaVersion);
        Assert.True(state.RestartRequired);
        Assert.True(state.StartAtSignIn);
        Assert.Equal(CompanyDataRoute.Undecided, state.CompanyDataRoute);
        Assert.False(state.Finished);
        Assert.Equal(FailureCode.None, state.LastFailure);
        Assert.Empty(state.Steps);
    }

    [Fact]
    public void ACorruptFile_IsTreatedAsFresh_AndTheEventIsReported()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not json");
        var store = MakeStore();
        var problems = new List<string>();
        store.Problem += problems.Add;

        var state = store.Read();

        Assert.False(state.RestartRequired);
        Assert.False(state.Finished);
        var problem = Assert.Single(problems);
        Assert.Contains("fresh state", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfAnUnknownSchema_IsTreatedAsFresh_AndTheEventIsReported()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"schemaVersion\":999,\"restartRequired\":true,\"finished\":true}");
        var store = MakeStore();
        var problems = new List<string>();
        store.Problem += problems.Add;

        var state = store.Read();

        Assert.False(state.RestartRequired);
        Assert.False(state.Finished);
        Assert.Single(problems);
    }

    [Fact]
    public void TheSetupFields_RoundTrip()
    {
        MakeStore().Write(new SetupState
        {
            StartAtSignIn = false,
            CompanyDataRoute = CompanyDataRoute.AdoptExisting,
            Finished = true,
            LastFailure = FailureCode.DiskFull,
            LastFailedStep = SetupStepId.Download,
            Steps = new() { [SetupStepId.Build] = SetupStepState.Done, [SetupStepId.Start] = SetupStepState.Failed },
        });

        var read = MakeStore().Read();

        Assert.False(read.StartAtSignIn);
        Assert.Equal(CompanyDataRoute.AdoptExisting, read.CompanyDataRoute);
        Assert.True(read.Finished);
        Assert.Equal(FailureCode.DiskFull, read.LastFailure);
        Assert.Equal(SetupStepId.Download, read.LastFailedStep);
        Assert.Equal(SetupStepState.Done, read.Steps[SetupStepId.Build]);
        Assert.Equal(SetupStepState.Failed, read.Steps[SetupStepId.Start]);
    }

    [Fact]
    public void AFreshState_IsOnForStartAtSignIn()
    {
        Assert.True(new SetupState().StartAtSignIn);
    }
}
