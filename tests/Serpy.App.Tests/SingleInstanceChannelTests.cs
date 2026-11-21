using Serpy.App.Platform.Windows;

namespace Serpy.App.Tests;

public sealed class SingleInstanceChannelTests
{
    [Fact]
    public void TwoAppDataRoots_ResolveDifferentPipeNames()
    {
        var pipe1 = SingleInstanceChannel.ResolvePipeName(@"C:\Users\Alice\AppData\Local\Serpy");
        var pipe2 = SingleInstanceChannel.ResolvePipeName(@"C:\Users\Bob\AppData\Local\Serpy");

        Assert.NotEqual(pipe1, pipe2);
        Assert.StartsWith("Serpy-Pipe-", pipe1);
        Assert.StartsWith("Serpy-Pipe-", pipe2);
    }

    [Theory]
    [InlineData("Show", SingleInstanceCommand.Show)]
    [InlineData("show", SingleInstanceCommand.Show)]
    [InlineData("ShowSetup", SingleInstanceCommand.ShowSetup)]
    [InlineData("--resume-setup", SingleInstanceCommand.ShowSetup)]
    [InlineData("OpenNotification", SingleInstanceCommand.OpenNotification)]
    public void ParseCommand_ValidCommands_ReturnsExpected(string input, SingleInstanceCommand expected)
    {
        var result = SingleInstanceChannel.ParseCommand(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("--unattended")]
    [InlineData("--fresh-install")]
    [InlineData("format c:")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseCommand_ForbiddenOrMalformed_ReturnsNull(string? input)
    {
        var result = SingleInstanceChannel.ParseCommand(input);
        Assert.Null(result);
    }

    [Fact]
    public async Task SendAndReceive_OverPipe_DispatchesCommand()
    {
        var pipeName = $"Serpy-Test-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<SingleInstanceCommand>();

        using var server = SingleInstanceChannel.StartServer(pipeName, cmd => received.TrySetResult(cmd), cts.Token);

        var sent = await SingleInstanceChannel.SendCommandAsync(pipeName, SingleInstanceCommand.ShowSetup, TimeSpan.FromSeconds(3));
        Assert.True(sent);

        var result = await received.Task;
        Assert.Equal(SingleInstanceCommand.ShowSetup, result);
    }
}
