using AwesomeAssertions;
using Tide.Application.Distribution;

namespace Tide.UnitTests;

/// <summary>The email templates' catalog, checks and filling in (Administration › Email templates).</summary>
public class EmailTemplateTests
{
    [Fact]
    public void EveryDefault_PassesItsOwnChecks_AndUsesOnlyItsOwnValues()
    {
        EmailTemplateCatalog.All.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        foreach (var t in EmailTemplateCatalog.All)
            foreach (var part in Enum.GetValues<EmailPart>())
                EmailTemplateCatalog.Problem(t, part, t.Default(part)).Should().BeNull($"{t.Id} {part}");
    }

    [Fact]
    public void AKey_NamesATemplateAndAPart()
    {
        EmailTemplateCatalog.Key("digest", EmailPart.Body).Should().Be("EmailTemplate:digest:Body");
        EmailTemplateCatalog.Parse("emailtemplate:DIGEST:subject").Should().Be((EmailTemplateCatalog.Digest, EmailPart.Subject));
        EmailTemplateCatalog.Parse("EmailTemplate:digest:Footer").Should().BeNull();
        EmailTemplateCatalog.Parse("EmailTemplate:nope:Body").Should().BeNull();
        EmailTemplateCatalog.Parse("EmailTemplate:digest:7").Should().BeNull("a number isn't a part's name");
        EmailTemplateCatalog.IsKey("Org:TeamName").Should().BeFalse();
    }

    [Theory]
    [InlineData(EmailPart.Subject, "", "Enter a subject.")]
    [InlineData(EmailPart.Subject, "TIDE: {{Nope}}", "{{Nope}} isn't something this email can fill in")]
    [InlineData(EmailPart.Subject, "TIDE: {{ActionList}}", "{{ActionList}} can only go in the body.")]
    [InlineData(EmailPart.Body, "Nothing to see here.", "The body needs {{ActionList}}")]
    public void AProblem_SaysWhatsWrong(EmailPart part, string text, string expected) =>
        EmailTemplateCatalog.Problem(EmailTemplateCatalog.Overdue, part, text).Should().Contain(expected);

    [Fact]
    public void TooLong_IsRefused_AndASubjectIsOneLine()
    {
        EmailTemplateCatalog.Problem(EmailTemplateCatalog.Overdue, EmailPart.Heading, new string('x', EmailTemplateCatalog.MaxLine + 1)).Should().Contain("at most");
        EmailTemplateCatalog.Problem(EmailTemplateCatalog.Overdue, EmailPart.Body, "{{ActionList}}" + new string('x', EmailTemplateCatalog.MaxBody)).Should().Contain("at most");
        EmailTemplateCatalog.Normalize(EmailPart.Subject, "  TIDE:\r\n late  ").Should().Be("TIDE: late");
        EmailTemplateCatalog.Normalize(EmailPart.Body, "a\r\nb\n").Should().Be("a\nb");
    }

    [Fact]
    public void FillingIn_EncodesText_ButNotTidesOwnMarkup_AndRendersTheMarkdown()
    {
        var t = EmailTemplateCatalog.RotationRequest;
        var r = EmailTemplateRenderer.Render(t, "TIDE: {{Requester}} asks {{Note}}", "{{Partner}}, please",
            "**{{Change}}**\n\n{{Note}}\n\nOpen it{{RotationLink}}. <b>typed</b> {{Unknown}}",
            new Dictionary<string, string>
            {
                ["Requester"] = "Sam <script>",
                ["Partner"] = "Robin",
                ["Change"] = "Robin covers <img src=x onerror=alert(1)>",
                ["Note"] = "<p style=\"margin:0;\">quoted</p>",
                ["RotationLink"] = " (<a href=\"https://tide.example/rotation\">open it</a>)",
            }, appUrl: null);

        r.Subject.Should().Be("TIDE: Sam <script> asks", "the subject is text (the mail library encodes it), and HTML values don't go in it");
        r.Heading.Should().Be("Robin, please");
        r.BodyHtml.Should().Contain("<strong>Robin covers &lt;img src=x onerror=alert(1)&gt;</strong>", "a value is text")
            .And.Contain("<p style=\"margin:0;\">quoted</p>", "TIDE's own markup goes in as it is")
            .And.NotContain("<p style=\"margin:0 0 12px;\"><p", "a value alone in its paragraph replaces it")
            .And.Contain("<a href=\"https://tide.example/rotation\">open it</a>")
            .And.Contain("&lt;b&gt;typed&lt;/b&gt;", "HTML typed into the template is shown, not used")
            .And.NotContain("{{");
    }

    [Fact]
    public void TheStore_UsesTheDefaults_UntilOneIsSaved()
    {
        var store = new EmailTemplateStore(null);
        store.Text(EmailTemplateCatalog.Digest, EmailPart.Subject).Should().Be(EmailTemplateCatalog.Digest.DefaultSubject);
        store.IsCustomized(EmailTemplateCatalog.Digest).Should().BeFalse();
        store.Render(EmailTemplateCatalog.Digest, new Dictionary<string, string> { ["Team"] = "Blue", ["WeekOf"] = "Nov 2" })
            .Subject.Should().Be("TIDE: Blue actions, week of Nov 2");
    }
}
