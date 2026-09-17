using Serpy.App.Platform;
using Serpy.App.Platform.Windows;

namespace Serpy.App.Tests;

public sealed class UnattendedArgsTests
{
    [Theory]
    [InlineData("--unattended", true)]
    [InlineData("--UNATTENDED", true)]
    [InlineData("--fresh-install", true)]
    [InlineData("--FRESH-INSTALL", true)]
    [InlineData("--tray", false)]
    [InlineData("", false)]
    public void UnattendedFlags_AreCorrectlyDetected(string arg, bool expected)
    {
        string[] args = string.IsNullOrEmpty(arg) ? [] : [arg];
        bool isUnattended = args.Contains("--unattended", StringComparer.OrdinalIgnoreCase) ||
                            args.Contains("--fresh-install", StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, isUnattended);
    }

    [Fact]
    public void ConsoleAttachment_TryAttachParent_DoesNotCrash()
    {
        // Safe invocation: may return true or false depending on whether caller has console attached,
        // but must never throw an exception or cause an access violation.
        var exception = Record.Exception(() => ConsoleAttachment.TryAttachParent());
        Assert.Null(exception);
    }
}
