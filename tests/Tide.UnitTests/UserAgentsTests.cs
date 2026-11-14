using AwesomeAssertions;
using Tide.Application.Intake;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class UserAgentsTests
{
    [Fact]
    public void TideOwn_NamesTheReleaseAndTheProject()
    {
        UserAgents.TideFor("1.14.0+0123456789abcdef", releaseBuild: true).Should().Be("TIDE/1.14.0 (+https://github.com/jashley92/Tide)");
        UserAgents.TideFor("1.15.0-rc.1+0123456789abcdef").Should().Be("TIDE/1.15.0-rc.1 (+https://github.com/jashley92/Tide)");
    }

    [Fact]
    public void TideOwn_ForADevelopmentBuild_SaysSo_WithItsCommit()
    {
        UserAgents.TideFor("1.0.0+a1b2c3d4e5f6").Should().Be("TIDE/dev+a1b2c3d (+https://github.com/jashley92/Tide)");
        UserAgents.TideFor("1.0.0").Should().Be("TIDE/dev (+https://github.com/jashley92/Tide)");
    }

    [Fact]
    public void TheBrowserPreset_IsAValidChromeOnWindowsString()
    {
        UserAgents.Browser.Should().StartWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)").And.Contain("Chrome/");
        UserAgents.Problem(UserAgents.Browser).Should().BeNull();
        UserAgents.Problem(UserAgents.Tide).Should().BeNull();
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; ExampleBot/2.1)")]
    [InlineData("  padded/1.0  ")]
    public void Problem_AcceptsPrintableAscii(string value) => UserAgents.Problem(value).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil/1.0\r\nX-Injected: yes")]
    [InlineData("evil/1.0\nX-Injected: yes")]
    [InlineData("evil/1.0\rX")]
    [InlineData("tab\there")]
    [InlineData("café/1.0")]
    [InlineData("nul\0")]
    [InlineData("del\u007f")]
    public void Problem_RefusesBlankNonAsciiAndControlCharacters(string value) => UserAgents.Problem(value).Should().NotBeNull();

    [Fact]
    public void Problem_RefusesTooLong()
    {
        UserAgents.Problem(new string('a', Source.MaxUserAgent)).Should().BeNull();
        UserAgents.Problem(new string('a', Source.MaxUserAgent + 1)).Should().Contain(Source.MaxUserAgent.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Global_FollowsTheChoice_AndFallsBackToTidesOwn()
    {
        UserAgents.Global(new IntakeOptions()).Should().Be(UserAgents.Tide, "TIDE's own is the default");
        UserAgents.Global(new IntakeOptions { UserAgent = UserAgentChoice.Browser }).Should().Be(UserAgents.Browser);
        UserAgents.Global(new IntakeOptions { UserAgent = UserAgentChoice.Custom, CustomUserAgent = " Acme-CTI/2 " }).Should().Be("Acme-CTI/2");
        UserAgents.Global(new IntakeOptions { UserAgent = UserAgentChoice.Custom, CustomUserAgent = "" }).Should().Be(UserAgents.Tide, "a custom choice with nothing typed");
        UserAgents.Global(new IntakeOptions { UserAgent = UserAgentChoice.Custom, CustomUserAgent = "a\r\nb: c" }).Should().Be(UserAgents.Tide, "never an invalid value");
        UserAgents.Global(new IntakeOptions { UserAgent = UserAgentChoice.Inherit }).Should().Be(UserAgents.Tide);
    }

    [Fact]
    public void ForASource_InheritIsNull_SoTheGlobalApplies()
    {
        UserAgents.For(null).Should().BeNull();
        UserAgents.For(new Source()).Should().BeNull("a source inherits by default");
        UserAgents.For(new Source { UserAgent = UserAgentChoice.Tide }).Should().Be(UserAgents.Tide);
        UserAgents.For(new Source { UserAgent = UserAgentChoice.Browser }).Should().Be(UserAgents.Browser);
        UserAgents.For(new Source { UserAgent = UserAgentChoice.Custom, CustomUserAgent = "Feedly/1.0" }).Should().Be("Feedly/1.0");
        UserAgents.For(new Source { UserAgent = UserAgentChoice.Custom, CustomUserAgent = "bad\nvalue" }).Should().BeNull();
    }

    [Fact]
    public void TheSettings_AreChecked_AsSaved()
    {
        OperationalSettings.Normalize(OperationalSettings.UserAgent, "browser").Should().Be("Browser");
        OperationalSettings.Normalize(OperationalSettings.UserAgent, "Tide").Should().Be("Tide");
        OperationalSettings.Problem(OperationalSettings.UserAgent, "Inherit").Should().NotBeNull("inheriting is a source's choice, not the global one");
        OperationalSettings.Problem(OperationalSettings.UserAgent, "2").Should().NotBeNull();
        OperationalSettings.Normalize(OperationalSettings.CustomUserAgent, "  Acme/1.0 ").Should().Be("Acme/1.0");
        OperationalSettings.Normalize(OperationalSettings.CustomUserAgent, "").Should().Be("");
        OperationalSettings.Problem(OperationalSettings.CustomUserAgent, "a\r\nX-Injected: 1").Should().NotBeNull();
    }
}
