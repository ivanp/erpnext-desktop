using Serpy.Core.Guest;

namespace Serpy.Core.Tests.Guest;

/// <summary>
/// The wording is what <c>bench scheduler status</c> prints in the pinned Frappe (16.31.0):
/// "Scheduler is enabled for site X" or "Scheduler is disabled for site X". The host once looked for
/// the word "active", which that command never prints, so a healthy site failed its health gate.
/// </summary>
public sealed class SchedulerStatusTests
{
    [Fact]
    public void AnEnabledScheduler_IsRunning()
    {
        Assert.True(GuestOperations.IsSchedulerEnabled("Scheduler is enabled for site site1.local\n"));
    }

    [Fact]
    public void ADisabledScheduler_IsNotRunning()
    {
        Assert.False(GuestOperations.IsSchedulerEnabled("Scheduler is disabled for site site1.local\n"));
    }

    [Fact]
    public void BenchNoticesAroundTheStatusLine_DoNotChangeTheAnswer()
    {
        const string output =
            "INFO: A newer version of bench is available: 5.31.0 -> 5.31.1\r\n" +
            "Scheduler is enabled for site site1.local\r\n";

        Assert.True(GuestOperations.IsSchedulerEnabled(output));
    }

    [Fact]
    public void ANoticeThatMentionsEnabled_IsNotTakenForTheStatus()
    {
        const string output =
            "INFO: the new scheduler is enabled by default in the next release\n" +
            "Scheduler is disabled for site site1.local\n";

        Assert.False(GuestOperations.IsSchedulerEnabled(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):\n  File ...\nOperationalError")]
    [InlineData("Scheduler is active for site site1.local")]
    public void OutputWithNoStatusWord_IsNotRunning(string output)
    {
        Assert.False(GuestOperations.IsSchedulerEnabled(output));
    }
}
