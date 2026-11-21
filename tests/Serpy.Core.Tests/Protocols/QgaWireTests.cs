using System.Text.Json;
using Serpy.Core.Protocols.Json;
using Serpy.Core.Protocols.Qga;
using Serpy.Core.Protocols.Qmp;

namespace Serpy.Core.Tests.Protocols;

/// <summary>
/// The guest agent and the monitor validate each message strictly. A field that is present with a
/// null value is a type error to them: the first real build failed on <c>"env": null</c>. These tests
/// pin the bytes that go on the wire.
/// </summary>
public sealed class QgaWireTests
{
    private static JsonElement Wire(QgaExecRequest request) =>
        JsonDocument.Parse(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.QgaExecRequest)).RootElement;

    [Fact]
    public void AnExecWithNoEnvironmentAndNoStdin_LeavesBothFieldsOut_InsteadOfWritingNull()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "python3.14", Arg = ["--version"], CaptureOutput = true },
        });

        var arguments = wire.GetProperty("arguments");
        Assert.False(arguments.TryGetProperty("env", out _), "env must be absent, not null");
        Assert.False(arguments.TryGetProperty("input-data", out _), "input-data must be absent, not null");
        Assert.Equal("python3.14", arguments.GetProperty("path").GetString());
        Assert.Equal("--version", arguments.GetProperty("arg")[0].GetString());
        Assert.True(arguments.GetProperty("capture-output").GetBoolean());
    }

    [Fact]
    public void AnExecWithNoArguments_LeavesArgOut()
    {
        var wire = Wire(new QgaExecRequest { Arguments = new QgaExecArgs { Path = "/bin/true" } });

        Assert.False(wire.GetProperty("arguments").TryGetProperty("arg", out _));
    }

    [Fact]
    public void AnExecWithStdin_KeepsItAsAString_AndNoOtherNullAppears()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "su", InputData = "c2VjcmV0" },
        });

        Assert.Equal("c2VjcmV0", wire.GetProperty("arguments").GetProperty("input-data").GetString());
        Assert.DoesNotContain("null", wire.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithAnEnvironment_WritesItAsAnArray()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "env", Env = ["A=1", "B=2"] },
        });

        var env = wire.GetProperty("arguments").GetProperty("env");
        Assert.Equal(JsonValueKind.Array, env.ValueKind);
        Assert.Equal(2, env.GetArrayLength());
    }

    [Fact]
    public void TheStatusRequest_CarriesOnlyThePid()
    {
        var json = JsonSerializer.Serialize(
            new QgaExecStatusRequest { Arguments = new QgaExecStatusArgs { Pid = 4242 } },
            ProtocolJsonContext.Default.QgaExecStatusRequest);

        Assert.Equal(4242, JsonDocument.Parse(json).RootElement.GetProperty("arguments").GetProperty("pid").GetInt32());
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AQmpCommandWithNoArguments_LeavesTheArgumentsOut()
    {
        var json = JsonSerializer.Serialize(
            new QmpRequest { Execute = "system_powerdown" }, ProtocolJsonContext.Default.QmpRequest);

        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.Equal("system_powerdown", JsonDocument.Parse(json).RootElement.GetProperty("execute").GetString());
    }
}
