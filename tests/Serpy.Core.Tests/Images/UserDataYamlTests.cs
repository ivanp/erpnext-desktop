using System.Text;
using Serpy.Core.Images;
using YamlDotNet.RepresentationModel;

namespace Serpy.Core.Tests.Images;

/// <summary>
/// The 2026-11-20 setup stall: cloud-init rejected build/cloud-init/user-data at line 19 as
/// invalid YAML, skipped write_files and runcmd, and the host waited for a sentinel that could
/// never arrive. These tests parse the rendered user-data with a real YAML parser.
/// </summary>
public sealed class UserDataYamlTests
{
    private static string Template =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cloud-init", "user-data"));

    private static string Guest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "guest", name));

    private static string RenderProduction() => NoCloudSeedWriter.RenderUserData(
        Template, Guest("provision.sh"), Guest("init-data.sh"), Guest("recover.sh"));

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlSequenceNode Seq(YamlMappingNode root, string key) =>
        (YamlSequenceNode)root.Children[new YamlScalarNode(key)];

    [Fact]
    public void RenderedProductionUserData_ParsesAsYaml_WithWriteFilesAndRunCmd()
    {
        var root = Parse(RenderProduction());

        Assert.True(root.Children.ContainsKey(new YamlScalarNode("write_files")));
        Assert.True(root.Children.ContainsKey(new YamlScalarNode("runcmd")));
    }

    [Fact]
    public void RenderedProductionUserData_RunCmd_HasExactlyOneEntryThatRunsTheProvisioningScript()
    {
        var runcmd = Seq(Parse(RenderProduction()), "runcmd");

        Assert.Single(runcmd.Children);
        Assert.Contains("provision.sh", runcmd.Children[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedProductionUserData_EmbeddedScripts_DecodeToTheSourceBytes()
    {
        var writeFiles = Seq(Parse(RenderProduction()), "write_files");
        var expected = new Dictionary<string, string>
        {
            ["/usr/local/bin/provision.sh"] = Guest("provision.sh"),
            ["/usr/local/bin/init-data.sh"] = Guest("init-data.sh"),
            ["/usr/local/bin/recover.sh"] = Guest("recover.sh"),
        };

        foreach (var (path, source) in expected)
        {
            var entry = writeFiles.Children.Cast<YamlMappingNode>()
                .Single(n => n.Children[new YamlScalarNode("path")].ToString() == path);
            Assert.Equal("b64", entry.Children[new YamlScalarNode("encoding")].ToString());
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(entry.Children[new YamlScalarNode("content")].ToString()));
            Assert.Equal(source.Replace("\r\n", "\n", StringComparison.Ordinal), decoded);
        }
    }

    [Fact]
    public void RenderUserData_ScriptWithCrlf_IsEmbeddedWithLfOnly()
    {
        const string template = "#cloud-config\nwrite_files:\n  - path: /x.sh\n    encoding: b64\n    content: @@SERPY_PROVISION@@\n  - path: /y.sh\n    encoding: b64\n    content: @@SERPY_INIT_DATA@@\n  - path: /z.sh\n    encoding: b64\n    content: @@SERPY_RECOVER@@\n";
        var rendered = NoCloudSeedWriter.RenderUserData(template, "#!/bin/bash\r\necho hi\r\n", "", "");

        var b64 = Parse(rendered).Children[new YamlScalarNode("write_files")] is YamlSequenceNode s
            ? ((YamlMappingNode)s.Children[0]).Children[new YamlScalarNode("content")].ToString() : "";
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));

        Assert.DoesNotContain('\r', decoded);
    }

    [Fact]
    public void RenderUserData_ScriptWithYamlHostileText_StillParses()
    {
        // Regression for build/cloud-init/user-data:19: 'Package: *' made the YAML an alias error.
        const string hostile = "#!/bin/bash\nprintf '%s\\n' 'Package: *' 'Pin: release a=unstable'\n# & * : - | > ' \"\n\tcat <<'EOF'\n- not: a list\nEOF\n";
        const string template = "#cloud-config\nwrite_files:\n  - path: /x.sh\n    encoding: b64\n    content: @@SERPY_PROVISION@@\n  - path: /y.sh\n    encoding: b64\n    content: @@SERPY_INIT_DATA@@\n  - path: /z.sh\n    encoding: b64\n    content: @@SERPY_RECOVER@@\n";

        var rendered = NoCloudSeedWriter.RenderUserData(template, hostile, "", "");
        var content = ((YamlMappingNode)((YamlSequenceNode)Parse(rendered)
            .Children[new YamlScalarNode("write_files")]).Children[0]).Children[new YamlScalarNode("content")].ToString();

        Assert.Equal(hostile, Encoding.UTF8.GetString(Convert.FromBase64String(content)));
    }

    [Fact]
    public void RenderUserData_TemplateMissingAPlaceholder_FailsAndNamesIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NoCloudSeedWriter.RenderUserData("#cloud-config\n", "a", "b", "c"));

        Assert.Contains("@@SERPY_", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTemplate_HoldsNoShellCommandLines()
    {
        // KTD1: no shell text passes through the YAML parser, so no list item may carry a command.
        var root = Parse(Template);
        foreach (var item in Seq(root, "runcmd").Children)
            Assert.DoesNotContain("apt-get", item.ToString(), StringComparison.Ordinal);
    }
}
