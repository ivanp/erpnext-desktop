using Serpy.Core.Platform.Windows;

namespace Serpy.Core.Tests.Platform;

public sealed class PowerRequestTests
{
    [Fact]
    public void PowerRequest_CanBeTakenAndReleasedTwiceInARow_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var power = new PowerRequest();

        using (var hold1 = power.Hold())
        {
            Assert.NotNull(hold1);
        }

        using (var hold2 = power.Hold())
        {
            Assert.NotNull(hold2);
        }
    }
}
