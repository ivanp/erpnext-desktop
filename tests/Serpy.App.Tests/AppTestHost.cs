using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Serpy.App.Tests.TestAppBuilder))]

namespace Serpy.App.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Serpy.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
}
