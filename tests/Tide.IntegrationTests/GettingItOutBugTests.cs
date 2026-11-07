using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Distribution;
using Tide.Infrastructure.Notifications;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round (the brief and getting it out): a pickup folder that exists but can't be written to says so, and isn't
/// backed off as a relay that doesn't answer.
/// </summary>
public class GettingItOutBugTests
{
    // Makes the folder one this process can't write a file into; false where that can't be done (running as root).
    private static bool Unwritable(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.WriteData,
                AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(dir, "probe.txt"), "x");
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    private static void Writable(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.WriteData,
                AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task APickupFolderThatCantBeWrittenTo_IsAFailedSendThatSaysSo_AndIsntBackedOffAsAnUnreachableRelay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tide-pickup-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (!Unwritable(dir)) return;   // root writes anywhere: nothing to show
            var options = new EmailOptions { Enabled = true, From = "tide@example.org", PickupDirectory = dir };
            var sender = new EmailSender(new StaticOptionsMonitor<EmailOptions>(options), NullLogger<EmailSender>.Instance, new OutboundCircuit());
            var email = new OutgoingEmail(["soc@example.org"], "s", "<p>h</p>", "t", []);

            for (var i = 0; i <= OutboundCircuit.FailuresToOpen; i++)
            {
                var result = await sender.SendAsync(email);
                result.Status.Should().Be(EmailSendStatus.Failed);
                result.Error.Should().Contain("Email:PickupDirectory", "it's the folder, not a relay, that's wrong")
                    .And.NotContain("SmtpHost").And.NotContain("hasn't been answering");
            }
        }
        finally
        {
            Writable(dir);
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// Bug round (the brief and getting it out), the rotation: a day whose meeting has been held is past for a late approval
/// and for an undo, as the brief already says who presented it.
/// </summary>
public class RotationHeldTodayBugTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Robin = "dev:robin";
    private readonly DateOnly _week1;

    public RotationHeldTodayBugTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _week1 = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
    }

    public void Dispose() => _db.Dispose();

    private async Task Roster()
    {
        var r = _db.Rotation();
        await r.AddMemberAsync(Me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        await r.SetWeekAsync(_week1, Me);
    }

    private void As(string id, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.Roles = roles.Length == 0 ? new HashSet<AppRole> { AppRole.Analyst } : roles.ToHashSet();
    }

    // Monday of next week at the hour given, in the organization's zone.
    private OrgClock Monday(int hour)
    {
        var local = _week1.ToDateTime(new TimeOnly(hour, 0));
        return new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(local, _db.Clock.Zone.GetUtcOffset(local))));
    }

    // Monday's meeting, held at 10:30 and presented by whoever the brief names.
    private async Task HeldMonday(string presenterId, string presenterName)
    {
        await using var ctx = _db.Context();
        var held = Monday(10).Now.AddMinutes(30).ToUniversalTime();
        ctx.Briefs.Add(new Brief
        {
            BriefDate = _week1, AnalystId = presenterId, AnalystName = presenterName, Status = BriefStatus.Presented,
            MeetingHeldAtUtc = held, PresentedAtUtc = held.AddMinutes(25),
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ACoverApprovedAfterTodaysMeeting_LeavesTodayWithWhoeverPresentedIt()
    {
        await Roster();
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(2) });
        await HeldMonday(Me, "Test Analyst");

        As(Robin);
        var afternoon = Monday(15);
        await _db.Rotation(afternoon).ApproveAsync(change.Id);

        var days = await _db.Rotation(afternoon).CalendarAsync(_week1, _week1.AddDays(2));
        days.Select(d => d.AnalystId).Should().Equal([Me, Robin, Robin], "Monday's meeting was held, by the analyst its brief names");
        await using var ctx = _db.Context();
        (await ctx.RotationChanges.SingleAsync(c => c.Id == change.Id)).GiveFrom.Should().Be(_week1.AddDays(1));
    }

    [Fact]
    public async Task ACoverApprovedAfterTodaysMeeting_ForTodayAlone_IsRefusedAsPassed()
    {
        await Roster();
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });
        await HeldMonday(Me, "Test Analyst");

        As(Robin);
        var approve = () => _db.Rotation(Monday(15)).ApproveAsync(change.Id);
        await approve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*passed*");
        (await _db.Rotation(Monday(15)).CalendarAsync(_week1, _week1)).Single().AnalystId.Should().Be(Me);
    }

    [Fact]
    public async Task AnUndoAfterTodaysMeeting_LeavesTodayWithWhoeverPresentedIt()
    {
        await Roster();
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(2) });
        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);
        await HeldMonday(Robin, "Robin");

        As(Me, AppRole.Admin);
        var afternoon = Monday(15);
        await _db.Rotation(afternoon).CancelAsync(change.Id);

        var days = await _db.Rotation(afternoon).CalendarAsync(_week1, _week1.AddDays(2));
        days.Select(d => d.AnalystId).Should().Equal([Robin, Me, Me], "Robin held Monday's meeting; only the days to come go back");
    }

    [Fact]
    public async Task UndoForAChangeHoldingOnlyToday_IsOfferedUntilTodaysMeeting_AndNotAfter_AsTheServiceDecides()
    {
        // The rotation page offered Undo while the change's day was today or later; after today's meeting the service
        // refused it ("nothing to undo"). The page now asks the service's rule.
        await Roster();
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });
        As(Robin);
        await _db.Rotation(Monday(8)).ApproveAsync(change.Id);
        As(Me, AppRole.Admin);
        change = (await _db.Rotation().ChangesAsync()).Single(c => c.Id == change.Id);

        (await _db.Rotation(Monday(8)).UndoableAsync([change])).Should().Contain(change.Id, "before the meeting today is still to come");
        await HeldMonday(Robin, "Robin");
        (await _db.Rotation(Monday(15)).UndoableAsync([change])).Should().BeEmpty("today's meeting was held: the change's only day has passed");
        var undo = () => _db.Rotation(Monday(15)).CancelAsync(change.Id);
        await undo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nothing to undo*");
    }

    [Fact]
    public async Task BeforeTodaysMeeting_TodayStillChangesHands()
    {
        await Roster();
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(1) });

        As(Robin);
        var morning = Monday(8);
        await _db.Rotation(morning).ApproveAsync(change.Id);

        (await _db.Rotation(morning).CalendarAsync(_week1, _week1.AddDays(1))).Select(d => d.AnalystId).Should().Equal(Robin, Robin);
    }
}
