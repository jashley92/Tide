using System.Text.RegularExpressions;
using AwesomeAssertions;
using Tide.Application.Operations;

namespace Tide.UnitTests;

/// <summary>The answers file Administration downloads has the template's keys, so a re-run of the installer from it reads the same settings.</summary>
public partial class InstallAnswersTemplateTests
{
    // Keys the download leaves out on purpose: BackupAccount is for Install-Database.ps1's backup grants, which this server can't know.
    private static readonly string[] NotWritten = ["BackupAccount"];

    private static IEnumerable<string> Keys(string psd1) => psd1.ReplaceLineEndings("\n").Split('\n')
        .Select(l => Key().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value);

    [Fact]
    public void The_download_has_every_key_the_template_has()
    {
        var template = File.ReadAllText(Path.Combine(DeployScriptTests.RepoRoot(), "deploy", "tide.config.template.psd1"));
        var written = Keys(InstallAnswers.Write(new InstallFacts())).ToHashSet();
        Keys(template).Except(NotWritten).Should().NotBeEmpty().And.OnlyContain(k => written.Contains(k));
        Keys(template).Should().Contain("InstallWebSockets");
    }

    [Fact]
    public void InstallWebSockets_Is_Off_With_The_Templates_Comment()
    {
        var text = InstallAnswers.Write(new InstallFacts { AppPath = "tide" }).ReplaceLineEndings("\n");
        text.Should().MatchRegex(@"\n    InstallWebSockets\s+= \$false\n");
        var template = File.ReadAllText(Path.Combine(DeployScriptTests.RepoRoot(), "deploy", "tide.config.template.psd1")).ReplaceLineEndings("\n");
        var comment = Regex.Match(template, @"((?:    #[^\n]*\n)+)    InstallWebSockets = \$false").Groups[1].Value;
        comment.Should().NotBeEmpty();
        text.Should().Contain(comment + "    InstallWebSockets");
    }

    [GeneratedRegex(@"^\s{4}([A-Za-z]+)\s*=")]
    private static partial Regex Key();
}
