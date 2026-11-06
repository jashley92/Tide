using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Api;
using Tide.Application.Files;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bytes that start like each kind of file the checks tell apart.</summary>
internal static class Samples
{
    public static byte[] Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 1, 2, 3];
    public static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];

    /// <summary>A harmless stand-in for a Windows program: "MZ" and some text, nothing that runs.</summary>
    public static byte[] FakeProgram => "MZ this is a harmless test file, not a program"u8.ToArray();

    public static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}

/// <summary>Owner decisions on files added to actions, through the services: hash and real-type checks, descriptions,
/// removing with a reason and restoring, who may do what, the paperclip counts, the API's listing and the live feed.</summary>
public class ActionFileServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private Guid _team;

    public ActionFileServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<ActionItem> NewAction(string title = "Sweep for the loader")
    {
        if (_team == Guid.Empty) _team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = title, Kind = ActionKind.IocSweep, TeamId = _team });
        return (await _db.Actions().GetAsync(number))!.Action;
    }

    private Task<ActionDetail?> Detail(ActionItem a) => _db.Actions().GetAsync(a.Number);

    private void As(string id, string name, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = name;
        _db.User.Roles = roles.ToHashSet();
    }

    private void AsAdmin() => As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst, AppRole.Admin);

    [Fact]
    public async Task ARiskyFile_WhoseNameHasInvisibleCharactersInItsExtension_IsStillJudgedByTheNameItIsStoredUnder()
    {
        var a = await NewAction();
        var added = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("update.ps​1",Samples.Text("Write-Host hi"))])).Single();
        added.FileName.Should().Be("update.ps1");
        added.RiskReason.Should().NotBeNull("it downloads as update.ps1, so it is a script");

        await _db.Actions().AddCommentAsync(a.Id, "Dropper", [new ScreenshotUpload("run.b­at",Samples.Text("echo hi"))]);
        var row = (await Detail(a))!.Files.Live.Single(f => f.FileName == "run.bat");
        row.RiskReason.Should().NotBeNull();
    }

    [Fact]
    public async Task AddingTenFilesWithLongNames_KeepsEveryHashInTheHistory()
    {
        var a = await NewAction();
        var uploads = Enumerable.Range(0, 10).Select(i => new ActionFileUpload(new string('n', 120) + i + ".log", Samples.Text("content " + i))).ToList();
        await _db.ActionFiles().AddAsync(a.Id, uploads);
        var history = string.Join(" | ", (await Detail(a))!.Action.Events.Select(e => e.Text));
        foreach (var u in uploads) history.Should().Contain(Samples.Sha(u.Bytes));
    }

    [Fact]
    public async Task AFile_IsStoredWithItsHashAndSize_AndTheHistoryAndAuditTrailNameTheHash()
    {
        var a = await NewAction();
        var bytes = Samples.Text("2026-11-05 10:01 beacon to 203.0.113.7");
        var added = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("proxy.log", bytes, "text/plain", "Proxy extract for the host")])).Single();

        added.Sha256.Should().Be(Samples.Sha(bytes));
        (added.SizeBytes, added.IsImage, added.RiskReason, added.TypeNote, added.DeclaredType).Should().Be((bytes.Length, false, null, null, "text/plain"));
        added.Description.Should().Be("Proxy extract for the host");
        _db.Store.OpenRead(added.FileId).Should().NotBeNull("the bytes are in the file store, as a comment's are");

        var d = (await Detail(a))!;
        var row = d.Files.Live.Should().ContainSingle().Which;
        (row.FileName, row.Sha256, row.Source, row.AddedByName, row.ContentType).Should().Be(("proxy.log", added.Sha256, "action", "Test Analyst", "application/octet-stream"));
        d.Action.Events.Should().Contain(e => e.Text.Contains("Added the file proxy.log") && e.Text.Contains(added.Sha256));

        await using var ctx = _db.Context();
        var audit = await ctx.AuditLog.Where(x => x.EntityType == nameof(ActionFile) && x.Action == "Added").SingleAsync();
        audit.ChangesJson.Should().Contain(added.Sha256).And.Contain("proxy.log");
    }

    [Fact]
    public async Task AnImageWhoseBytesDontMatch_IsNeverShownInline()
    {
        var a = await NewAction();
        var files = _db.ActionFiles();
        var real = (await files.AddAsync(a.Id, [new ActionFileUpload("console.png", Samples.Png, "image/png")])).Single();
        var fake = (await files.AddAsync(a.Id, [new ActionFileUpload("console2.png", Samples.Text("not an image at all"), "image/png")])).Single();
        var other = (await files.AddAsync(a.Id, [new ActionFileUpload("console3.png", Samples.Jpeg, "image/png")])).Single();

        (real.IsImage, real.TypeNote, real.ShowsInline).Should().Be((true, null, true));
        fake.IsImage.Should().BeFalse();
        fake.TypeNote.Should().Be("It's named as a PNG image, but its contents aren't one.");
        other.TypeNote.Should().Be("It's named as a PNG image, but its contents are a JPEG image.");
        other.ShowsInline.Should().BeFalse("its bytes don't match the image type it claims");

        var shots = _db.Screenshots();
        (await shots.OpenAsync(real.FileId)).Should().NotBeNull("a real image, named as one, is shown inline");
        (await shots.OpenAsync(fake.FileId)).Should().BeNull();
        (await shots.OpenAsync(other.FileId)).Should().BeNull();
        var live = (await Detail(a))!.Files.Live;
        live.Single(f => f.FileId == real.FileId).ShowsInline.Should().BeTrue();
        live.Where(f => f.FileId != real.FileId).Should().OnlyContain(f => !f.ShowsInline);
        live.Single(f => f.FileId == real.FileId).ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ADescription_IsSetAtUpload_EditedLater_AndAudited()
    {
        var a = await NewAction();
        var files = _db.ActionFiles();
        var f = (await files.AddAsync(a.Id, [new ActionFileUpload("pcap.pcapng", Samples.Text("pcap"), null, "  First capture  ")])).Single();
        f.Description.Should().Be("First capture");

        await files.DescribeAsync(f.FileId, "Capture from the edge firewall, 10:00 to 10:15");
        (await Detail(a))!.Files.Live.Single().Description.Should().Be("Capture from the edge firewall, 10:00 to 10:15");
        await files.Invoking(x => x.DescribeAsync(f.FileId, new string('x', ActionFile.MaxDescription + 1)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*500 characters*");
        await files.Invoking(x => x.AddAsync(a.Id, [new ActionFileUpload("b.txt", Samples.Text("b"), null, new string('y', 501))]))
            .Should().ThrowAsync<ArgumentException>();
        await files.DescribeAsync(f.FileId, "   ");
        (await Detail(a))!.Files.Live.Single().Description.Should().BeNull("blank clears it");

        var events = (await Detail(a))!.Action.Events.Select(e => e.Text).ToList();
        events.Should().Contain("Described pcap.pcapng: “Capture from the edge firewall, 10:00 to 10:15”").And.Contain("Cleared the description of pcap.pcapng.");
        await using var ctx = _db.Context();
        (await ctx.AuditLog.CountAsync(x => x.EntityType == nameof(ActionFile) && x.Action == "Modified")).Should().Be(2);

        // A comment's file takes a description too.
        await _db.Actions().AddCommentAsync(a.Id, "The log", [new ScreenshotUpload("extract.log", Samples.Text("log"))]);
        var onComment = (await Detail(a))!.Files.Live.Single(r => r.OnComment);
        await files.DescribeAsync(onComment.FileId, "From the mail gateway");
        (await Detail(a))!.Files.Live.Single(r => r.OnComment).Description.Should().Be("From the mail gateway");
    }

    [Fact]
    public async Task Removing_NeedsAReason_KeepsTheBytes_AndOnlyAnAdministratorRestores()
    {
        var a = await NewAction();
        var files = _db.ActionFiles();
        var f = (await files.AddAsync(a.Id, [new ActionFileUpload("wrong.txt", Samples.Text("wrong host"))])).Single();

        await files.Invoking(x => x.RemoveAsync(f.FileId, "  ")).Should().ThrowAsync<ArgumentException>().WithMessage("Say why*");
        await files.RemoveAsync(f.FileId, "Uploaded to the wrong action");

        var d = (await Detail(a))!;
        d.Files.Live.Should().BeEmpty();
        var removed = d.Files.Removed.Should().ContainSingle().Which;
        (removed.RemovedByName, removed.RemovedReason, removed.Sha256).Should().Be(("Test Analyst", "Uploaded to the wrong action", f.Sha256));
        d.Action.Events.Should().Contain(e => e.Text == $"Removed the file wrong.txt (SHA-256 {f.Sha256}): Uploaded to the wrong action");
        _db.Store.OpenRead(f.FileId).Should().NotBeNull("the bytes are kept");
        await using (var ctx = _db.Context()) (await ctx.Files.AnyAsync(x => x.Id == f.FileId)).Should().BeTrue();

        As("S-1-5-21-TEST-2", "Robin Analyst", AppRole.Analyst);
        (await Detail(a))!.Files.Removed.Should().BeEmpty("only administrators see removed files");
        (await files.OpenAsync(f.FileId)).Should().BeNull("a removed file isn't served to an analyst");
        await files.Invoking(x => x.RestoreAsync(f.FileId)).Should().ThrowAsync<ForbiddenException>();

        AsAdmin();
        (await files.OpenAsync(f.FileId)).Should().NotBeNull("an administrator can still read what was removed");
        await files.RestoreAsync(f.FileId);
        var back = (await Detail(a))!;
        back.Files.Live.Should().ContainSingle(x => x.FileId == f.FileId);
        back.Files.Removed.Should().BeEmpty();
        back.Action.Events.Should().Contain(e => e.Text.StartsWith("Restored the file wrong.txt (removed by Test Analyst on"));
    }

    [Fact]
    public async Task ACommentsFile_IsRemovedOnItsOwn_WithAReason_AndOnlyAnAdministratorRestoresIt()
    {
        var a = await NewAction();
        await _db.Teams().AddMemberAsync(_team, "S-1-5-21-OWNER", "Olive Owner");
        await _db.Teams().AddMemberAsync(_team, "S-1-5-21-OWNER2", "Oscar Owner");
        As("S-1-5-21-OWNER", "Olive Owner", AppRole.ActionOwner);
        await _db.Actions().AddCommentAsync(a.Id, "Log and shot", [new ScreenshotUpload("extract.log", Samples.Text("log")), new ScreenshotUpload("shot.png", Samples.Png)]);
        var files = _db.ActionFiles();
        var live = (await Detail(a))!.Files.Live;
        var log = live.Single(f => f.FileName == "extract.log");
        var shot = live.Single(f => f.FileName == "shot.png");
        log.CanRemove.Should().BeTrue("its comment's author added it");

        As("S-1-5-21-OWNER2", "Oscar Owner", AppRole.ActionOwner);
        (await Detail(a))!.Files.Live.Single(f => f.FileId == log.FileId).CanRemove.Should().BeFalse();
        await files.Invoking(x => x.RemoveAsync(log.FileId, "Not mine")).Should().ThrowAsync<ForbiddenException>();

        As("S-1-5-21-OWNER", "Olive Owner", AppRole.ActionOwner);
        await files.Invoking(x => x.RemoveAsync(log.FileId, " ")).Should().ThrowAsync<ArgumentException>().WithMessage("Say why*");
        await files.RemoveAsync(log.FileId, "Wrong host's log");
        As("S-1-5-21-TEST-2", "Robin Analyst", AppRole.Analyst);
        await files.RemoveAsync(shot.FileId, "Shows a customer's name");

        var d = (await Detail(a))!;
        d.Files.Live.Should().BeEmpty();
        d.Files.Removed.Should().BeEmpty("only an administrator sees removed files");
        d.Action.Events.Should().Contain(e => e.Text.StartsWith("Removed the file extract.log (SHA-256 ") && e.Text.EndsWith("from a comment by Olive Owner: Wrong host's log"));
        d.Action.Comments.Single().Files.Should().HaveCount(2).And.OnlyContain(f => f.IsRemoved, "the records stay");
        _db.Store.Files.Should().ContainKeys(log.FileId, shot.FileId);
        (await _db.Screenshots().OpenAsync(shot.FileId)).Should().BeNull("a removed comment screenshot isn't shown inline");
        (await _db.Screenshots().OpenAttachmentAsync(log.FileId)).Should().BeNull();
        (await files.OpenAsync(log.FileId)).Should().BeNull("an analyst doesn't download a removed file");
        (await _db.Actions().ListAsync(new ActionFilter())).Single(r => r.Id == a.Id).Files.Should().Be(0, "the paperclip counts live files only");
        (await _db.Api().ActionAsync(a.Number, null))!.Files.Should().BeEmpty("the API lists live files only");
        await files.Invoking(x => x.RestoreAsync(log.FileId)).Should().ThrowAsync<ForbiddenException>();

        AsAdmin();
        var removed = (await Detail(a))!.Files.Removed;
        removed.Should().HaveCount(2).And.OnlyContain(f => f.OnComment);
        removed.Single(f => f.FileId == log.FileId).Should().Match<ActionFileRow>(f => f.RemovedByName == "Olive Owner" && f.RemovedReason == "Wrong host's log");
        await using (var opened = await files.OpenAsync(log.FileId)) opened.Should().NotBeNull("an administrator can still read it");
        await files.RestoreAsync(log.FileId);
        await files.RestoreAsync(log.FileId);
        var back = (await Detail(a))!;
        back.Files.Live.Should().ContainSingle(f => f.FileId == log.FileId && f.OnComment);
        back.Action.Events.Should().Contain(e => e.Text.StartsWith("Restored the file extract.log to a comment by Olive Owner (removed by Olive Owner on"));
        await files.RemoveAsync(shot.FileId, "Again");
        (await Detail(a))!.Files.Removed.Single().RemovedReason.Should().Be("Shows a customer's name", "removing it again changes nothing");
    }

    [Fact]
    public async Task DeletingAComment_KeepsItsFilesAsRemoved_ForAnAdministratorToRestore()
    {
        var a = await NewAction();
        await _db.Actions().AddCommentAsync(a.Id, "Evidence", [new ScreenshotUpload("mail.eml", Samples.Text("mail")), new ScreenshotUpload("old.txt", [1])]);
        var before = (await Detail(a))!;
        var comment = before.Action.Comments.Single();
        var mail = before.Files.Live.Single(f => f.FileName == "mail.eml");
        var old = before.Files.Live.Single(f => f.FileName == "old.txt");
        await _db.ActionFiles().RemoveAsync(old.FileId, "Not needed");
        await _db.ActionFiles().DescribeAsync(mail.FileId, "The original message");

        await _db.Actions().DeleteCommentAsync(comment.Id);

        var d = (await Detail(a))!;
        d.Action.Comments.Should().BeEmpty();
        d.Files.Live.Should().BeEmpty();
        d.Action.Events.Should().Contain(e => e.Text == "Test Analyst deleted a comment by Test Analyst; its 2 files are kept as removed.");
        var keptMail = d.Files.Removed.Single(f => f.FileId == mail.FileId);
        (keptMail.RemovedReason, keptMail.FromCommentBy, keptMail.Sha256, keptMail.Description, keptMail.OnComment)
            .Should().Be((ActionFile.CommentDeletedReason, "Test Analyst", mail.Sha256, "The original message", false));
        d.Files.Removed.Single(f => f.FileId == old.FileId).RemovedReason.Should().Be("Not needed", "one removed already keeps why");
        _db.Store.Files.Should().ContainKeys(mail.FileId, old.FileId);
        await using (var ctx = _db.Context()) (await ctx.Files.CountAsync(f => f.Id == mail.FileId || f.Id == old.FileId)).Should().Be(2);

        // The store's clean-up of unused evidence leaves them alone.
        await _db.Screenshots().DiscardEvidenceAsync(mail.FileId);
        _db.Store.Files.Should().ContainKey(mail.FileId);

        (await _db.Actions().ListAsync(new ActionFilter())).Single(r => r.Id == a.Id).Files.Should().Be(0);
        await _db.ActionFiles().RestoreAsync(mail.FileId);
        var back = (await Detail(a))!.Files.Live.Single();
        (back.FileId, back.OnComment, back.FromCommentBy).Should().Be((mail.FileId, false, "Test Analyst"));
        (await _db.Actions().ListAsync(new ActionFilter())).Single(r => r.Id == a.Id).Files.Should().Be(1);
    }

    [Fact]
    public async Task WhoMay_AddRemoveAndDownload()
    {
        var a = await NewAction();
        var other = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await _db.Teams().AddMemberAsync(_team, "S-1-5-21-OWNER", "Olive Owner");
        await _db.Teams().AddMemberAsync(_team, "S-1-5-21-OWNER2", "Oscar Owner");
        await _db.Teams().AddMemberAsync(other, "S-1-5-21-ELSEWHERE", "Elsa Elsewhere");
        var analysts = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("analyst.txt", Samples.Text("a"))])).Single();
        var files = _db.ActionFiles();

        // An ActionOwner on the owning team adds and describes, and removes only their own file.
        As("S-1-5-21-OWNER", "Olive Owner", AppRole.ActionOwner);
        var olives = (await files.AddAsync(a.Id, [new ActionFileUpload("olive.txt", Samples.Text("o"))])).Single();
        await files.DescribeAsync(analysts.FileId, "Described by the owner");
        (await Detail(a))!.Files.Live.Single(f => f.FileId == analysts.FileId).CanRemove.Should().BeFalse();
        await files.Invoking(x => x.RemoveAsync(analysts.FileId, "Mine now")).Should().ThrowAsync<ForbiddenException>();
        (await files.OpenAsync(analysts.FileId)).Should().NotBeNull();

        // A teammate who didn't add it can't remove it either.
        As("S-1-5-21-OWNER2", "Oscar Owner", AppRole.ActionOwner);
        await files.Invoking(x => x.RemoveAsync(olives.FileId, "Tidying")).Should().ThrowAsync<ForbiddenException>();

        As("S-1-5-21-OWNER", "Olive Owner", AppRole.ActionOwner);
        await files.RemoveAsync(olives.FileId, "Wrong capture");

        // A member of another team reads and downloads, but can't add or describe.
        As("S-1-5-21-ELSEWHERE", "Elsa Elsewhere", AppRole.ActionOwner);
        await files.Invoking(x => x.AddAsync(a.Id, [new ActionFileUpload("elsa.txt", Samples.Text("e"))])).Should().ThrowAsync<ForbiddenException>();
        await files.Invoking(x => x.DescribeAsync(analysts.FileId, "Mine")).Should().ThrowAsync<ForbiddenException>();
        await files.Invoking(x => x.RemoveAsync(analysts.FileId, "Mine")).Should().ThrowAsync<ForbiddenException>();
        await using (var download = await files.OpenAsync(analysts.FileId)) download.Should().NotBeNull("anyone who reads the action downloads its files");

        // A Reader doesn't read actions at all.
        As("S-1-5-21-READER", "Rhea Reader", AppRole.Reader);
        (await files.OpenAsync(analysts.FileId)).Should().BeNull();
        await files.Invoking(x => x.ListAsync(a.Id)).Should().ThrowAsync<ForbiddenException>();

        // An analyst removes anyone's.
        As("S-1-5-21-TEST-2", "Robin Analyst", AppRole.Analyst);
        await files.RemoveAsync(analysts.FileId, "Superseded");
        (await Detail(a))!.Files.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Limits_AreTheCommentsOnes_AndCountedPerAction()
    {
        var a = await NewAction();
        var files = _db.ActionFiles();
        await files.Invoking(x => x.AddAsync(a.Id, [new ActionFileUpload("big.png", [.. Samples.Png, .. new byte[1024 * 1024]])]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*1 MB limit for images*");
        await files.Invoking(x => x.AddAsync(a.Id, [new ActionFileUpload("big.bin", new byte[2 * 1024 * 1024 + 1])]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*2 MB limit for files*");
        await files.Invoking(x => x.AddAsync(a.Id, [new ActionFileUpload("empty.txt", [])])).Should().ThrowAsync<ArgumentException>().WithMessage("*empty*");
        await files.Invoking(x => x.AddAsync(a.Id, Enumerable.Range(0, 11).Select(i => new ActionFileUpload($"{i}.txt", [1])).ToList()))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Add at most 10 files at once.");
        files.LimitsText.Should().Be("Up to 2 MB a file (1 MB for an image).");
        (await Detail(a))!.Files.Live.Should().BeEmpty("nothing was added");
        _db.Store.Files.Count.Should().Be(0, "nothing was stored either");
    }

    [Fact]
    public async Task ThePaperclip_CountsLiveFilesOnTheActionAndItsComments_InTheRowsQuery()
    {
        var a = await NewAction("With files");
        var b = await NewAction("Without files");
        var files = _db.ActionFiles();
        var added = await files.AddAsync(a.Id, [new ActionFileUpload("one.txt", [1]), new ActionFileUpload("two.txt", [2])]);
        await _db.Actions().AddCommentAsync(a.Id, "And a log", [new ScreenshotUpload("three.log", [3])]);
        await files.RemoveAsync(added[0].FileId, "Duplicate");

        var rows = await _db.Actions().ListAsync(new ActionFilter());
        rows.Single(r => r.Id == a.Id).Files.Should().Be(2, "two.txt and the comment's file; the removed one isn't counted");
        rows.Single(r => r.Id == b.Id).Files.Should().Be(0);
        (await _db.Actions().BoardAsync(new ActionFilter())).Cards.Single(r => r.Id == a.Id).Files.Should().Be(2);
    }

    [Fact]
    public async Task TheApi_ListsTheLiveFiles_AndOpensOnlyOnesOnThatAction()
    {
        var a = await NewAction();
        var b = await NewAction("Another");
        var f = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("loader.exe", Samples.FakeProgram, "application/x-msdownload", "Sample")])).Single();
        await _db.Actions().AddCommentAsync(a.Id, "Log", [new ScreenshotUpload("extract.log", Samples.Text("log"))]);
        var gone = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("gone.txt", [1])])).Single();
        await _db.ActionFiles().RemoveAsync(gone.FileId, "Wrong");

        var api = await _db.Api().ActionAsync(a.Number, null, "/tide/api/v1");
        api!.Files.Should().HaveCount(2);
        var file = api.Files!.Single(x => x.Source == "action");
        (file.Id, file.Name, file.Size, file.Sha256, file.ContentType, file.DeclaredType, file.AddedBy, file.Description, file.Risky)
            .Should().Be((f.FileId, "loader.exe", Samples.FakeProgram.Length, f.Sha256, "application/octet-stream", "application/x-msdownload", "Test Analyst", "Sample", true));
        file.RiskReason.Should().Be("it's a Windows program");
        file.DownloadUrl.Should().Be($"/tide/api/v1/actions/{a.Number}/files/{f.FileId}");
        var log = api.Files!.Single(x => x.Source == "comment");
        log.CommentId.Should().NotBeNull();
        log.Sha256.Should().Be(Samples.Sha(Samples.Text("log")));

        await using (var opened = await _db.Api().OpenFileAsync(a.Number, f.FileId, null)) opened.Should().NotBeNull();
        (await _db.Api().OpenFileAsync(b.Number, f.FileId, null)).Should().BeNull("the file isn't on that action");
        (await _db.Api().OpenFileAsync(a.Number, gone.FileId, null)).Should().BeNull("a removed file isn't listed or served");
        (await _db.Api().ActionsAsync(ActionView.Open, null)).Should().OnlyContain(x => x.Files == null, "a list doesn't carry files");
    }

    [Fact]
    public async Task AddingRemovingAndDescribing_AreHeard_OnTheActionAndItsLists()
    {
        var a = await NewAction();
        var heard = new List<Change>();
        _db.Feed.Changed += c => { lock (heard) heard.Add(c); };
        var files = _db.ActionFiles();

        var f = (await files.AddAsync(a.Id, [new ActionFileUpload("a.txt", [1])])).Single();
        heard.Select(c => c.Topic).Should().Contain([ChangeFeed.Action(a.Id), ChangeFeed.Actions]);
        heard.Clear();
        await files.RemoveAsync(f.FileId, "Wrong");
        heard.Select(c => c.Topic).Should().Contain([ChangeFeed.Action(a.Id), ChangeFeed.Actions]);
        heard.Clear();
        await files.RestoreAsync(f.FileId);
        heard.Select(c => c.Topic).Should().Contain(ChangeFeed.Action(a.Id));

        await _db.Actions().AddCommentAsync(a.Id, "Log", [new ScreenshotUpload("x.log", [1])]);
        var onComment = (await Detail(a))!.Files.Live.Single(r => r.OnComment);
        heard.Clear();
        await files.DescribeAsync(onComment.FileId, "The log");
        heard.Select(c => c.Topic).Should().Contain(ChangeFeed.Action(a.Id), "the history line it writes is heard");
    }

    [Fact]
    public async Task ACommentsNewFile_IsJudgedForRisk_AndAnOlderOneByItsName()
    {
        var a = await NewAction();
        await _db.Actions().AddCommentAsync(a.Id, "Sample", [new ScreenshotUpload("readme.txt", Samples.FakeProgram, "text/plain")]);
        (await Detail(a))!.Files.Live.Single().RiskReason.Should().Be("it's a Windows program", "its bytes say so, whatever its name");

        // One stored before the risk was recorded is judged by its name.
        await using (var ctx = _db.Context())
        {
            var file = await ctx.ActionCommentFiles.SingleAsync();
            file.RiskReason = null;
            file.FileName = "dropper.ps1";
            await ctx.SaveChangesAsync();
        }
        var row = (await Detail(a))!.Files.Live.Single();
        row.RiskReason.Should().Be("a .ps1 file can run on the computer that opens it");
        _db.ActionFiles().ZipFirst(row).Should().BeTrue();
    }

    [Fact]
    public async Task ARemovedFile_CanBeRemovedAgainAsNothing_AndAnUnknownOneIsNotFound()
    {
        var a = await NewAction();
        var files = _db.ActionFiles();
        var f = (await files.AddAsync(a.Id, [new ActionFileUpload("a.txt", [1])])).Single();
        await files.RemoveAsync(f.FileId, "Once");
        await files.RemoveAsync(f.FileId, "Twice");
        (await Detail(a))!.Files.Removed.Single().RemovedReason.Should().Be("Once");
        await files.RestoreAsync(f.FileId);
        await files.RestoreAsync(f.FileId);
        await files.Invoking(x => x.RemoveAsync(Guid.NewGuid(), "Why")).Should().ThrowAsync<KeyNotFoundException>();
        await files.Invoking(x => x.RestoreAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
        await files.Invoking(x => x.DescribeAsync(Guid.NewGuid(), "x")).Should().ThrowAsync<KeyNotFoundException>();
        await files.Invoking(x => x.AddAsync(Guid.NewGuid(), [new ActionFileUpload("a.txt", [1])])).Should().ThrowAsync<KeyNotFoundException>();
        await files.Invoking(x => x.RemoveAsync(f.FileId, new string('r', 501))).Should().ThrowAsync<ArgumentException>();
        (await files.AddAsync(a.Id, [])).Should().BeEmpty();
        (await files.OpenAsync(Guid.NewGuid())).Should().BeNull();
        (await files.ListAsync(a.Id)).Live.Should().ContainSingle();
    }

    [Fact]
    public async Task AFileUsedOnAnAction_IsNeverTakenAsAHuntsEvidence()
    {
        var a = await NewAction();
        var f = (await _db.ActionFiles().AddAsync(a.Id, [new ActionFileUpload("shot.png", Samples.Png)])).Single();
        await _db.Screenshots().DiscardEvidenceAsync(f.FileId);
        _db.Store.OpenRead(f.FileId).Should().NotBeNull("an action's file isn't unused evidence");
    }
}

/// <summary>What makes a file risky, the type notes, the zip's choice and entry name, and the setting's checks.</summary>
public class FileRiskTests
{
    [Theory]
    [InlineData("loader.exe", null, "a .exe file can run on the computer that opens it")]
    [InlineData("invoice.pdf.LNK", null, "a .lnk file can run on the computer that opens it")]
    [InlineData("update.hta", null, "a .hta file can run on the computer that opens it")]
    [InlineData("budget.xlsm", null, "a .xlsm file can run on the computer that opens it")]
    [InlineData("setup.msi", null, "a .msi file can run on the computer that opens it")]
    [InlineData("run.ps1", null, "a .ps1 file can run on the computer that opens it")]
    [InlineData("report", "application/vnd.ms-word.document.macroEnabled.12", "an Office file with macros can run code")]
    [InlineData("report", "application/x-msdownload; charset=binary", "it was sent as application/x-msdownload, which can run on the computer that opens it")]
    public void ByNameOrDeclaredType(string name, string? type, string reason) => FileRisk.Reason(name, type).Should().Be(reason);

    [Fact]
    public void ByTheFirstBytes_WhateverTheName()
    {
        FileRisk.Reason("notes.txt", "text/plain", Samples.FakeProgram).Should().Be("it's a Windows program");
        FileRisk.Reason("notes.txt", null, [0x7F, (byte)'E', (byte)'L', (byte)'F']).Should().Be("it's a Linux program");
        FileRisk.Reason("notes.txt", null, [0xCF, 0xFA, 0xED, 0xFE]).Should().Be("it's a macOS or Java program");
        FileRisk.Reason("notes.txt", null, "#!/bin/sh"u8).Should().Be("it's a script");
        FileRisk.Reason("old.doc", null, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]).Should().StartWith("it's a legacy Office");
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("capture.pcapng", null)]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("shot.png", "image/png")]
    [InlineData(null, null)]
    [InlineData("folder.exe/notes", null)]
    public void HarmlessFiles_AreNot(string? name, string? type) => FileRisk.Reason(name, type, "hello"u8).Should().BeNull();

    [Fact]
    public void TypeNotes_SayWhereTheNameOrTypeAndTheBytesDisagree()
    {
        FileRisk.Inspect("a.png", null, Samples.Png).Should().Be(new FileInspection("image/png", null, null));
        FileRisk.Inspect("a.png", null, Samples.Png).ShowsInline.Should().BeTrue();
        FileRisk.Inspect("a", "image/jpeg", Samples.Text("text")).TypeNote.Should().Be("It's sent as a JPEG image, but its contents aren't one.");
        FileRisk.Inspect("a", "image/png", Samples.Jpeg).TypeNote.Should().Be("It was sent as a PNG image, but its contents are a JPEG image.");
        FileRisk.Inspect("a.jpg", "image/jpg", Samples.Jpeg).TypeNote.Should().BeNull("image/jpg is JPEG");
        FileRisk.Inspect("notes.txt", null, Samples.Png).TypeNote.Should().Be("Its contents are a PNG image, not what its name says.");
        FileRisk.Inspect("a.gif", null, Samples.Png).TypeNote.Should().Be("It's named as a GIF image, but its contents are a PNG image.");
        FileRisk.Inspect("a.webp", null, Samples.Png).TypeNote.Should().Contain("WebP");
        FileRisk.Inspect("drawing.svg", "image/svg+xml", Samples.Text("<svg/>")).Should().Match<FileInspection>(i => i.TypeNote == null && i.RiskReason != null);
        FileRisk.Inspect("shot.exe", null, Samples.Png).ShowsInline.Should().BeFalse("it's named to run");
    }

    [Fact]
    public void TheZip_IsTheMainDownload_AsTheSettingSays()
    {
        FileRisk.ZipFirst(ZipDownloads.RiskyOnly, "it's a Windows program").Should().BeTrue();
        FileRisk.ZipFirst(ZipDownloads.RiskyOnly, null).Should().BeFalse();
        FileRisk.ZipFirst(ZipDownloads.Always, null).Should().BeTrue();
        FileRisk.ZipFirst(ZipDownloads.Never, "it's a Windows program").Should().BeFalse();
        FileRisk.Sentence("it's a Windows program").Should().Be("It's a Windows program.");
        FileRisk.Sentence(null).Should().BeNull();
    }

    [Theory]
    [InlineData("loader.exe", "loader.exe")]
    [InlineData(@"C:\Users\x\loader.exe", "loader.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a<b>:c?.txt", "abc.txt")]
    [InlineData("...", "file.bin")]
    [InlineData(null, "file.bin")]
    public void TheEntryName_HasNoFolderOrReservedCharacter(string? name, string entry) => FileRisk.EntryName(name).Should().Be(entry);

    [Fact]
    public void TheSetting_IsCheckedAndListedForTheBundle()
    {
        OperationalSettings.Normalize(OperationalSettings.ZipDownloads, "always").Should().Be("Always");
        OperationalSettings.Normalize(OperationalSettings.ZipDownloads, " Never ").Should().Be("Never");
        OperationalSettings.Problem(OperationalSettings.ZipDownloads, "1").Should().NotBeNull();
        OperationalSettings.Problem(OperationalSettings.ZipDownloads, "Sometimes").Should().Be("Zipped downloads are RiskyOnly, Always or Never.");
        SettingsCatalog.InApp.Should().ContainSingle(s => s.Key == "Files:ZipDownloads" && s.Group == "Files" && s.EditedIn == "admin/teams");
        SettingsCatalog.IsInApp("Files:ZipDownloads").Should().BeTrue("it travels in the configuration bundle");
        new FileStoreOptions().ZipDownloads.Should().Be(ZipDownloads.RiskyOnly, "the default");
    }
}

/// <summary>The downloads over HTTP: per-file permission, the plain file's headers, the zip opening with its password,
/// and the API's listing and download held to the token's scope.</summary>
public class ActionFileHttpTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Outsider = new("dev:files-elsewhere", "Elsa Elsewhere", "ActionOwner");

    private async Task<(int Number, Guid ActionId, Guid FileId)> ActionWithFile(string name, byte[] bytes, string? declared = null)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var team = (await sp.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = "files-http " + name, TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        var file = (await sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload(name, bytes, declared, "A test file")])).Single();
        return (number, id, file.FileId);
    }

    [Fact]
    public async Task ThePlainFile_IsAnAttachment_NeverSniffed_AndSandboxed()
    {
        var bytes = Samples.Text("just a log line");
        var (_, _, fileId) = await ActionWithFile("plain.log", bytes);
        var res = await app.CreateClient().GetAsync($"/files/{fileId}/download");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Content.Headers.ContentDisposition.FileNameStar.Should().Be("plain.log");
        res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("sandbox").And.Contain("default-src 'none'");
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        (await app.CreateClient().GetAsync($"/files/{fileId}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "it isn't an image");
    }

    [Fact]
    public async Task ARiskyFile_DownloadsInAZip_ThatOpensWithThePasswordInfected()
    {
        var bytes = Samples.FakeProgram;
        var (_, _, fileId) = await ActionWithFile("not-really.exe", bytes);
        var res = await app.CreateClient().GetAsync($"/files/{fileId}/zip");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        res.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("not-really.exe.zip");
        res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        var zipped = await res.Content.ReadAsByteArrayAsync();

        using var zip = new ZipFile(new MemoryStream(zipped)) { Password = FileRisk.ZipPassword };
        var entry = zip.Cast<ZipEntry>().Should().ContainSingle().Which;
        (entry.Name, entry.IsCrypted, entry.AESKeySize).Should().Be(("not-really.exe", true, 0), "classic ZipCrypto, which File Explorer opens");
        using var read = new MemoryStream();
        await using (var s = zip.GetInputStream(entry)) await s.CopyToAsync(read);
        read.ToArray().Should().Equal(bytes);

        using var locked = new ZipFile(new MemoryStream(zipped)) { Password = "wrong" };
        locked.Invoking(z => { using var s = z.GetInputStream(z.Cast<ZipEntry>().Single()); s.ReadByte(); }).Should().Throw<Exception>("it needs the password");
    }

    [Fact]
    public async Task ADownload_IsCheckedPerFile()
    {
        var (_, _, fileId) = await ActionWithFile("checked.txt", Samples.Text("x"));
        (await app.CreateClient().GetAsync($"/files/{Guid.NewGuid()}/zip")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var outsider = SignedIn(app, Outsider);
        var files = outsider.ServiceProvider.GetRequiredService<ActionFileService>();
        await using (var d = await files.OpenAsync(fileId)) d.Should().NotBeNull("an owner on another team reads every action");
        await using var reader = SignedIn(app, new SignIn("dev:files-reader", "Rhea Reader", "Reader"));
        (await reader.ServiceProvider.GetRequiredService<ActionFileService>().OpenAsync(fileId)).Should().BeNull();
    }

    [Fact]
    public async Task TheApi_ListsFilesAndDownloadsThem_UnderTheTokensScope()
    {
        var bytes = Samples.FakeProgram;
        var (number, _, fileId) = await ActionWithFile("api.exe", bytes, "application/octet-stream");
        string reader, briefsOnly;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            reader = (await tokens.IssueAsync("files reader", [ApiScopes.ActionsRead], TlpLevel.Amber, 1)).Token;
            briefsOnly = (await tokens.IssueAsync("briefs only", [ApiScopes.BriefsRead], TlpLevel.Amber, 1)).Token;
        }
        HttpClient Client(string token)
        {
            var c = app.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        var action = await Client(reader).GetFromJsonAsync<ApiAction>($"/api/v1/actions/{number}");
        var file = action!.Files.Should().ContainSingle().Which;
        (file.Id, file.Name, file.Sha256, file.Risky, file.Source, file.Description).Should().Be((fileId, "api.exe", Samples.Sha(bytes), true, "action", "A test file"));
        file.DownloadUrl.Should().Be($"/api/v1/actions/{number}/files/{fileId}");

        var plain = await Client(reader).GetAsync(file.DownloadUrl);
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        plain.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        (await plain.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        var zipped = await Client(reader).GetAsync(file.DownloadUrl + "?zip=true");
        zipped.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        (await Client(briefsOnly).GetAsync(file.DownloadUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the token needs actions:read");
        (await Client(reader).GetAsync($"/api/v1/actions/{number}/files/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var other = await ActionWithFile("other.txt", [1]);
        (await Client(reader).GetAsync($"/api/v1/actions/{other.Number}/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the file is on another action");

        var described = await Client(reader).GetStringAsync("/api/v1/openapi.json");
        described.Should().Contain("/actions/{number}/files/{fileId}").And.Contain("\"ActionFile\"");
    }
}

/// <summary>The Files section on the action page and the setting on the admin page.</summary>
public class ActionFilePageTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<(int Number, Guid Id)> NewAction(string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var team = (await sp.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = title, TeamId = team });
        return (number, (await actions.GetAsync(number))!.Action.Id);
    }

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Rendered<ActionPage>> Open(int number) =>
        RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });

    private Task SetZip(ZipDownloads mode) => As(async sp =>
    {
        await sp.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [OperationalSettings.ZipDownloads] = mode.ToString() });
        return true;
    });

    [Fact]
    public async Task APresentedAction_DoesNotListACommentFileThatWasRemoved()
    {
        var (number, id) = await NewAction("files-page: present removed");
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            await actions.AddCommentAsync(id, "Two", [new ScreenshotUpload("kept-evidence.log", Samples.Text("k")), new ScreenshotUpload("gone-evidence.log", Samples.Text("g"))]);
            var files = sp.GetRequiredService<ActionFileService>();
            var gone = (await actions.GetAsync(number))!.Files.Live.Single(f => f.FileName == "gone-evidence.log");
            await files.RemoveAsync(gone.FileId, "Wrong host");
            return true;
        });
        var detail = await As(sp => sp.GetRequiredService<ActionService>().GetAsync(number));
        await using var page = await RenderAsync<PresentAction>(app, "/present", new() { ["Detail"] = detail, ["Links"] = true });
        (await page.Html()).Should().Contain("</span> kept-evidence.log</div>").And.NotContain("</span> gone-evidence.log</div>");
    }

    [Fact]
    public async Task TheFilesSection_ListsBothKinds_WithSourceHashAndDownloads()
    {
        var (number, id) = await NewAction("files-page: both kinds");
        var risky = await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id,
            [new ActionFileUpload("sample.exe", Samples.FakeProgram, null, "The dropper from the mailbox")]));
        await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().AddCommentAsync(id, "Here's the log", [new ScreenshotUpload("gateway.log", Samples.Text("log"))]);
            return true;
        });

        await SetZip(ZipDownloads.RiskyOnly);
        await using var page = await Open(number);
        var html = await page.Html();
        html.Should().Contain("Files <span class=\"tide-count tide-muted fw-normal\">(2)</span>");
        html.Should().Contain("Added by Tester</span>").And.Contain("On a comment by Tester</a>");
        html.Should().Contain($"actions/{number}#comment-").And.Contain("id=\"comment-");
        html.Should().Contain("The dropper from the mailbox").And.Contain(risky[0].Sha256 + "</code>");
        html.Should().Contain($"files/{risky[0].FileId}/zip").And.Contain("aria-label=\"Download sample.exe in a zip\"").And.Contain("aria-label=\"Download sample.exe as is\"")
            .And.Contain("Warning: It&#x27;s a Windows program.");
        html.Should().Contain("Up to 25 MB a file (15 MB for an image).");
        html.Should().Contain("Remove gateway.log", "a comment's file is removed on its own too");
        html.Should().Contain("It opens with the password &quot;infected&quot;.").And.NotContain("7-Zip");
        html.Should().NotContain("Removed files");
    }

    [Fact]
    public async Task TheFilesSection_IsInTheSideColumn_AfterAbout_WithEachFilesDetailsBehindMore()
    {
        var (number, id) = await NewAction("files-page: side column");
        var added = await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id,
            [new ActionFileUpload("a-rather-long-capture-name-from-the-edge-firewall-on-the-second-floor.pcapng", Samples.Text("pcap"), null, "Edge capture, 10:00 to 10:15")]));
        var f = added.Single();

        await using var page = await Open(number);
        var html = await page.Html();
        int At(string text) => html.IndexOf(text, StringComparison.Ordinal);
        var side = At("<div class=\"col-lg-4\">");
        side.Should().BeGreaterThan(0);
        At("id=\"files\"").Should().BeGreaterThan(side, "Files are in the side column")
            .And.BeGreaterThan(At("<h2>About</h2>"), "under About")
            .And.BeGreaterThan(At("<h2>Comments"), "so when the columns stack they come after Comments")
            .And.BeLessThan(At("<h2>History</h2>"), "and before History");
        html.Should().Contain($"title=\"{f.FileName}\"", "a long name is cut short with the full name in its title");

        var more = html[At($"id=\"file-more-{f.FileId}\"")..];
        more = more[..more.IndexOf("</li>", StringComparison.Ordinal)];
        more.Should().Contain("hidden").And.Contain("Edge capture, 10:00 to 10:15").And.Contain(f.Sha256 + "</code>")
            .And.Contain("Edit the description of").And.Contain("Remove " + f.FileName);
        html.Should().Contain($"aria-controls=\"file-more-{f.FileId}\"").And.Contain("aria-expanded=\"false\"")
            .And.Contain($"aria-label=\"More about {f.FileName}\"");
    }

    [Fact]
    public void TheMoreButton_OpensAndCloses_AndAnIconSaysWhatTheFileIs()
    {
        var row = new ActionFileRow(Guid.NewGuid(), "a.txt", 1, null, null, "Tester", DateTimeOffset.UtcNow, null, false, null, null);
        var panel = new ActionFilesPanel();
        panel.IsOpen(row).Should().BeFalse();
        panel.Toggle(row);
        panel.IsOpen(row).Should().BeTrue();
        panel.SetDraft(row.FileId, "Typing", null, null);
        panel.Toggle(row);
        panel.IsOpen(row).Should().BeFalse("closing it drops the description being typed");
        panel.StartRemove(row);
        panel.IsOpen(row).Should().BeTrue("removing opens it for the reason");
        panel.StartDescribe(row);
        panel.IsOpen(row).Should().BeTrue();

        ActionFilesPanel.Icon(row).Should().Be("bi-paperclip");
        ActionFilesPanel.Icon(row with { ShowsInline = true }).Should().Be("bi-image");
        ActionFilesPanel.Icon(row with { RiskReason = "program" }).Should().Be("bi-exclamation-triangle");
    }

    [Fact]
    public async Task TheSetting_MakesEveryFileOrNoneAZip()
    {
        var (number, id) = await NewAction("files-page: setting");
        var files = await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id,
            [new ActionFileUpload("calm.txt", Samples.Text("calm")), new ActionFileUpload("tool.exe", Samples.FakeProgram)]));
        try
        {
            await SetZip(ZipDownloads.Always);
            await using (var page = await Open(number))
            {
                var html = await page.Html();
                html.Should().Contain($"files/{files[0].FileId}/zip").And.Contain($"files/{files[1].FileId}/zip");
                html.Should().Contain("The file itself, not zipped.");
            }

            await SetZip(ZipDownloads.Never);
            await using (var page = await Open(number))
            {
                var html = await page.Html();
                html.Should().NotContain("/zip\"").And.NotContain(" as is\"");
                html.Should().Contain($"files/{files[1].FileId}/download");
            }
        }
        finally { await SetZip(ZipDownloads.RiskyOnly); }
    }

    [Fact]
    public async Task RemovedFiles_ShowToAnAdministrator_Collapsed_WithRestore()
    {
        var (number, id) = await NewAction("files-page: removed");
        var f = await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload("old.txt", [1])]));
        await As(async sp => { await sp.GetRequiredService<ActionFileService>().RemoveAsync(f[0].FileId, "Wrong host's log"); return true; });

        await using (var page = await Open(number))
        {
            var html = await page.Html();
            html.Should().Contain("<summary>Removed files (1)</summary>").And.Contain("Wrong host&#x27;s log").And.Contain("Restore old.txt");
        }
        await using (var analyst = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number },
                         new SignIn("dev:files-analyst", "Robin", "Analyst")))
            (await analyst.Html()).Should().NotContain("Removed files");
    }

    [Fact]
    public async Task ThePanel_AddsDescribesRemovesAndRestores()
    {
        var (number, id) = await NewAction("files-page: panel");
        Task<ActionFileList> List() => As(sp => sp.GetRequiredService<ActionFileService>().ListAsync(id));
        await using var panel = await RenderAsync<ActionFilesPanel>(app, "/actions/" + number,
            new() { ["Files"] = ActionFileList.None, ["ActionId"] = id, ["Number"] = number, ["CanWork"] = true });

        await panel.Click(p =>
        {
            ((List<FilePicker.PendingFile>)p.Pending).Add(FilePicker.PendingFile.From("picked.txt", Samples.Text("picked"), "text/plain"));
            return p.Add();
        });
        var row = (await List()).Live.Single();
        row.FileName.Should().Be("picked.txt");
        var toasts = panel.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts;
        toasts.Should().Contain(t => t.Message == "Added picked.txt.");

        await panel.Click(p => { p.SetDraft(row.FileId, "Picked from disk", null, null); return p.SaveDescription(row); });
        (await List()).Live.Single().Description.Should().Be("Picked from disk");

        await panel.Click(p => { p.SetDraft(null, null, row.FileId, "  "); return p.Remove(row); });
        (await List()).Live.Should().ContainSingle("a blank reason is refused");
        await panel.Click(p => { p.SetDraft(null, null, row.FileId, "Not needed"); return p.Remove(row); });
        (await List()).Live.Should().BeEmpty();
        await panel.Click(p => p.Restore(row));
        (await List()).Live.Should().ContainSingle();

        ActionFilesPanel.AsIsTitle(row).Should().Be("The file itself, not zipped.");
        FilePicker.PendingFile.From("x.exe", Samples.FakeProgram, null).RiskSentence.Should().Be("It's a Windows program.");
    }

    [Fact]
    public async Task ANewAction_TakesFilesFromTheRaiseForm()
    {
        await using var page = await RenderAsync<ActionPage>(app, "/actions/new", new() { ["Number"] = (int?)null });
        (await page.Html()).Should().Contain("Attach files").And.Contain("new-files-zone");
        var team = await As(async sp => (await sp.GetRequiredService<TeamService>().ListAsync())[0].Id);
        await page.Click(p =>
        {
            p.NewFiles.Add(FilePicker.PendingFile.From("raised.txt", Samples.Text("raised with it"), null));
            typeof(ActionPage).GetField("_form", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(p, new ActionForm { Title = "files-page: raised with a file", TeamId = team });
            return p.Create();
        });
        var created = await As(async sp => (await sp.GetRequiredService<ActionService>().ListAsync(new ActionFilter()))
            .Single(r => r.Title == "files-page: raised with a file"));
        created.Files.Should().Be(1);
    }

    [Fact]
    public async Task TheAdminPage_SavesTheZipSetting()
    {
        await using var page = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        var html = await page.Html();
        html.Should().Contain("Action files").And.Contain("Files that can run (the default)").And.Contain("Never: every file downloads as it is");
        try
        {
            await page.Click(p => { p.Zip = ZipDownloads.Always; return p.SaveZip(); });
            (await As(sp => Task.FromResult(sp.GetRequiredService<ActionFileService>().ZipMode))).Should().Be(ZipDownloads.Always);
        }
        finally { await SetZip(ZipDownloads.RiskyOnly); }
    }

    [Fact]
    public async Task ThePaperclip_ShowsOnTheList_AndOnTheBoard()
    {
        var (_, id) = await NewAction("files-page: paperclip");
        await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload("one.txt", [1]), new ActionFileUpload("two.txt", [2])]));
        var rows = await As(sp => sp.GetRequiredService<ActionService>().ListAsync(new ActionFilter()));
        var row = rows.Single(r => r.Id == id);
        row.Files.Should().Be(2);
        await using var table = await RenderAsync<ActionTable>(app, "/actions", new() { ["Rows"] = (IReadOnlyList<ActionRow>)[row], ["Today"] = DateOnly.FromDateTime(DateTime.Today) });
        (await table.Html()).Should().Contain("bi-paperclip").And.Contain("title=\"2 files\"");
    }

    [Fact]
    public async Task AColleaguesFile_ReachesAnOpenActionPage()
    {
        var (number, id) = await NewAction("files-page: live");
        await using var page = await Open(number);
        (await page.Html()).Should().Contain("No files yet.");
        await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload("colleague.txt", [1])]),
            new SignIn("dev:files-robin", "Robin", "Analyst"));
        await page.Click(p => p.OnChangedElsewhere([new Change(ChangeFeed.Action(id), "dev:files-robin", "Robin", Guid.NewGuid())]));
        (await page.Html()).Should().Contain("colleague.txt").And.Contain("Added by Robin");
    }
}
