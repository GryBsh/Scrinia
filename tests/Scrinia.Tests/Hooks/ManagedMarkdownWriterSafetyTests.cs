using System.Text;
using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

public sealed class ManagedMarkdownWriterSafetyTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_markdown_{Guid.NewGuid():N}");

    public ManagedMarkdownWriterSafetyTests() => Directory.CreateDirectory(_workspace);

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    public static IEnumerable<object[]> MalformedDocuments()
    {
        string[] documents =
        [
            "<!-- scrinia:begin v1 -->\r\nUSER_CUSTOM_RULE\r\n",
            "USER_CUSTOM_RULE\r\n<!-- scrinia:end -->",
            "<!-- scrinia:end -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:begin v1 -->",
            "<!-- scrinia:begin v1 -->\r\n<!-- scrinia:begin v1 -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:end -->",
            "<!-- scrinia:begin v1 -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:end -->\r\n<!-- scrinia:end -->",
            "<!-- scrinia:begin v1 -->\r\nfirst\r\n<!-- scrinia:end -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:begin v1 -->\r\nsecond\r\n<!-- scrinia:end -->",
            "<!-- scrinia:begin v1 -->\r\nfirst\r\n<!-- scrinia:end -->\r\n<!-- scrinia:begin v1 -->\r\nUSER_CUSTOM_RULE",
            "<!-- scrinia:begin v2 -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:end -->",
            "<!-- scrinia:begin v2 -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:begin v1 -->\r\nfirst\r\n<!-- scrinia:end -->",
            "<!-- scrinia:begin v1 -->\r\nUSER_CUSTOM_RULE\r\n<!-- scrinia:end v2 -->",
        ];
        foreach (bool claude in new[] { false, true })
        foreach (string document in documents)
            yield return [claude, "# User\r\n" + document];
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void Install_RefusesAmbiguousMarkers_OnEveryAttempt_WithoutChangingBytes(bool claude, string document)
    {
        var (writer, path) = CreateWriter(claude);
        File.WriteAllText(path, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        byte[] original = File.ReadAllBytes(path);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            writer.Install(_workspace).Should().BeFalse();
            File.ReadAllBytes(path).Should().Equal(original);
        }

        writer.Uninstall(_workspace).Should().BeFalse();
        File.ReadAllBytes(path).Should().Equal(original);
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void Uninstall_RefusesAmbiguousMarkers_WithoutChangingBytes(bool claude, string document)
    {
        var (writer, path) = CreateWriter(claude);
        File.WriteAllText(path, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        byte[] original = File.ReadAllBytes(path);

        writer.Uninstall(_workspace).Should().BeFalse();

        File.ReadAllBytes(path).Should().Equal(original);
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void GetStatus_AmbiguousMarkers_ReportDriftForUninstallDetection(bool claude, string document)
    {
        var (writer, path) = CreateWriter(claude);
        File.WriteAllText(path, document);

        writer.GetStatus(_workspace).Should().Be(HookStatus.Drift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidBlock_UpdatesIdempotently_AndPreservesSurroundingText(bool claude)
    {
        var (writer, path) = CreateWriter(claude);
        const string prefix = "# User\r\nUSER_CUSTOM_RULE\r\n\r\n";
        const string suffix = "\r\n\r\n# Footer\r\nKeep this too.\r\n";
        File.WriteAllText(path, prefix + "<!-- scrinia:begin v1 -->\nold\n<!-- scrinia:end -->" + suffix);

        writer.Install(_workspace).Should().BeTrue();
        string updated = File.ReadAllText(path);
        updated.Should().StartWith(prefix).And.EndWith(suffix);
        writer.GetStatus(_workspace).Should().Be(HookStatus.Installed);
        writer.Install(_workspace).Should().BeTrue();
        File.ReadAllText(path).Should().Be(updated);

        writer.Uninstall(_workspace).Should().BeTrue();
        File.ReadAllText(path).Should().Be((prefix + suffix).Trim() + "\n");
    }

    private (IAgentArtifactInstaller Writer, string Path) CreateWriter(bool claude) => claude
        ? (new ClaudeMdWriter(), Path.Combine(_workspace, "CLAUDE.md"))
        : (new AgentsMdWriter(), Path.Combine(_workspace, "AGENTS.md"));
}
