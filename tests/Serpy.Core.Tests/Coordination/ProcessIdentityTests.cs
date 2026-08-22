using System.Diagnostics;
using Serpy.Core.Coordination;

namespace Serpy.Core.Tests.Coordination;

public sealed class ProcessIdentityTests
{
    [Fact]
    public void CurrentProcess_MatchesItsOwnStartTime()
    {
        var proc = Process.GetCurrentProcess();
        var ticks = ProcessIdentity.StartTimeTicks(proc);
        Assert.True(ProcessIdentity.IsAlive(proc.Id, ticks));
    }

    [Fact]
    public void CurrentProcess_WrongStartTime_ReturnsFalse()
    {
        var proc = Process.GetCurrentProcess();
        // Offset by 2 minutes — well beyond the 1-second tolerance.
        var wrongTicks = proc.StartTime.ToUniversalTime().AddMinutes(-2).Ticks;
        Assert.False(ProcessIdentity.IsAlive(proc.Id, wrongTicks));
    }

    [Fact]
    public void NonExistentPid_ReturnsFalse()
    {
        // int.MaxValue is a PID that cannot exist on any OS; returns false, no throw.
        Assert.False(ProcessIdentity.IsAlive(int.MaxValue, DateTime.UtcNow.Ticks));
    }
}
