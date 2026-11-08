using AwesomeAssertions;
using Tide.Application.Operations;

namespace Tide.UnitTests.Audit;

/// <summary>The install answers file stays a valid PowerShell data file whatever quotes a value has.</summary>
public sealed class InstallAnswersQuoteTests
{
    // PowerShell ends a single-quoted string at any of these; doubling one makes it literal.
    [Theory]
    [InlineData((char)0x0027)]
    [InlineData((char)0x2018)]
    [InlineData((char)0x2019)]
    [InlineData((char)0x201A)]
    [InlineData((char)0x201B)]
    public void Escape_EverySingleQuotePowerShellKnows_IsDoubled(char quote) =>
        InstallAnswers.Escape($"Robin{quote}s team").Should().Be($"Robin{quote}{quote}s team");

    [Fact]
    public void Write_ATeamNameWithATypographicApostrophe_StaysInsideItsString()
    {
        var text = InstallAnswers.Write(new InstallFacts { TeamName = "Robin" + (char)0x2019 + "s team" });

        var line = text.Split('\n').Single(l => l.TrimStart().StartsWith("TeamName", StringComparison.Ordinal)).TrimEnd('\r');
        line.Should().EndWith("= 'Robin" + (char)0x2019 + (char)0x2019 + "s team'");
    }

    [Fact]
    public void Escape_LineBreaks_AreDropped() => InstallAnswers.Escape("a\r\nb").Should().Be("a b");
}

/// <summary>The v1.0.0 release reads as a release, though a local build is stamped 1.0.0 as well.</summary>
public sealed class ReleaseBuildVersionTests
{
    [Fact]
    public void ReleaseOf_OneZeroZero_FromTheReleaseWorkflow_IsThatRelease()
    {
        AppVersionInfo.ReleaseOf("1.0.0+abcdef0123", releaseBuild: true).Should().Be(new ReleaseVersion(1, 0, 0));
        AppVersionInfo.DisplayOf("1.0.0+abcdef0123", releaseBuild: true).Should().Be("1.0.0 (abcdef0)");
    }

    [Fact]
    public void ReleaseOf_OneZeroZero_FromALocalBuild_IsStillADevelopmentBuild() =>
        AppVersionInfo.ReleaseOf("1.0.0+abcdef0123").Should().BeNull();

    [Fact]
    public void IsReleaseBuild_ATestBuild_IsNot() => AppVersionInfo.IsReleaseBuild.Should().BeFalse();
}

/// <summary>The exported answers file keeps whether the install checks the SQL Server's certificate.</summary>
public sealed class InstallAnswersCertificateTests
{
    [Theory]
    [InlineData("Server=SQL01;Database=Tide;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True", false)]
    [InlineData("Server=SQL01;Database=Tide;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=False", true)]
    [InlineData("Server=SQL01;Database=Tide;Trusted_Connection=True;Encrypt=True", true)]
    public void FromSettings_ReadsTrustServerCertificate(string connection, bool validates)
    {
        var facts = InstallFacts.FromSettings("SqlServer", connection, true, null, null, null, null);

        facts.ValidateServerCertificate.Should().Be(validates);
        InstallAnswers.Write(facts).Should().Contain("ValidateServerCertificate = " + (validates ? "$true" : "$false"));
    }
}

/// <summary>Agent 5's acceptance test for A5-R-8: the other identities every Windows sign-in token carries are flagged
/// as everyone-groups too.</summary>
public sealed class BundleRiskWindowsIdentitiesTests
{
    private static string? Risk(string group, Tide.Domain.Enums.AppRole role) => Tide.Application.Operations.ConfigBundleService.RiskOf(group, role);

    [Theory]
    [InlineData("NT AUTHORITY\\NETWORK")]
    [InlineData("S-1-5-2")]
    [InlineData("NT AUTHORITY\\INTERACTIVE")]
    [InlineData("S-1-5-4")]
    [InlineData("NT AUTHORITY\\This Organization")]
    [InlineData("S-1-5-15")]
    [InlineData("S-1-18-1")]
    [InlineData("BUILTIN\\Pre-Windows 2000 Compatible Access")]
    [InlineData("S-1-5-32-554")]
    public void TheOtherEveryoneIdentities_OfAWindowsToken_AreFlaggedToo(string group) =>
        Risk(group, Tide.Domain.Enums.AppRole.Analyst).Should().NotBeNull(group).And.Contain("everyone", group);
}
