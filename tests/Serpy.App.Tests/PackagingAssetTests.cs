namespace Serpy.App.Tests;

public sealed class PackagingAssetTests
{
    [Fact]
    public void AppBuildOutput_ContainsProvisioningAssets()
    {
        var root = AppContext.BaseDirectory;

        Assert.True(File.Exists(Path.Combine(root, "config", "versions.yaml")));
        Assert.True(File.Exists(Path.Combine(root, "cloud-init", "user-data")));
        Assert.True(File.Exists(Path.Combine(root, "cloud-init", "meta-data")));
        Assert.True(File.Exists(Path.Combine(root, "guest", "init-data.sh")));
        Assert.True(File.Exists(Path.Combine(root, "guest", "recover.sh")));
        Assert.True(File.Exists(Path.Combine(root, "guest", "provision-done.sh")));
    }
}
