using System.Data.Common;
using System.Text;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>What this server knows about how it was installed, for <see cref="InstallAnswers.Write"/>. A null value isn't
/// known here and is left for the administrator to fill in.</summary>
public sealed record InstallFacts
{
    public string? SqlInstance { get; init; }
    public string? DbName { get; init; }
    public string? AppAccount { get; init; }
    public bool AppMigrates { get; init; } = true;

    /// <summary>Whether the connection checks the SQL Server's certificate (TrustServerCertificate isn't on).</summary>
    public bool ValidateServerCertificate { get; init; }
    public string? AppPoolName { get; init; }
    public string? Hostname { get; init; }
    public string? SitePath { get; init; }
    public string? DataRoot { get; init; }
    public string? AppPath { get; init; }

    /// <summary>The AD groups mapped to each role (by name; a SID can't go in the answers file).</summary>
    public IReadOnlyDictionary<AppRole, IReadOnlyList<string>> Groups { get; init; } = new Dictionary<AppRole, IReadOnlyList<string>>();

    public string? TimeZone { get; init; }
    public string? MeetingTime { get; init; }
    public string? TeamName { get; init; }

    /// <summary>"Exported from TIDE 0.1.0-alpha.2 on … by …", for the file's first line.</summary>
    public string Origin { get; init; } = "";

    /// <summary>
    /// The facts the server's configuration gives: the SQL Server instance and database from the connection string (its
    /// server and database only, never a credential), the data root the installer derives the screenshot and key folders
    /// from, and the host name from the address readers use.
    /// </summary>
    public static InstallFacts FromSettings(string? provider, string? connectionString, bool applyMigrations, string? filesRoot,
        string? keyPath, string? baseUrl, string? pathBase)
    {
        string? instance = null, database = null;
        var validate = false;
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(connectionString))
        {
            var b = new DbConnectionStringBuilder();
            try
            {
                b.ConnectionString = connectionString;
                instance = First(b, "Server", "Data Source", "Address", "Addr", "Network Address");
                database = First(b, "Database", "Initial Catalog");
                // SqlClient checks the certificate unless TrustServerCertificate is set to true.
                validate = !string.Equals(First(b, "TrustServerCertificate", "Trust Server Certificate"), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { /* not readable: both stay unknown */ }
        }
        return new InstallFacts
        {
            SqlInstance = instance,
            DbName = database,
            AppMigrates = applyMigrations,
            ValidateServerCertificate = validate,
            DataRoot = Parent(filesRoot, "files") ?? Parent(keyPath, "dp-keys"),
            Hostname = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : null,
            AppPath = string.IsNullOrWhiteSpace(pathBase) ? null : pathBase.Trim('/'),
        };
    }

    private static string? First(DbConnectionStringBuilder b, params string[] keys) =>
        keys.Select(k => b.TryGetValue(k, out var v) ? v?.ToString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // A Windows path with a drive or a share, whatever OS reads it (Path.IsPathRooted doesn't know "E:\" off Windows).
    private static bool IsAbsolute(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith('/');

    // The installer puts the screenshots in <DataRoot>\files and the key ring in <DataRoot>\dp-keys.
    private static string? Parent(string? path, string leaf)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsAbsolute(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut > 0 && string.Equals(trimmed[(cut + 1)..], leaf, StringComparison.OrdinalIgnoreCase) ? trimmed[..cut] : null;
    }
}

/// <summary>
/// Writes an install answers file (<c>tide.config.psd1</c>, the shape of <c>deploy/tide.config.template.psd1</c>) from
/// what this server knows, for re-running <c>Install-Tide.ps1</c> to repair or upgrade it, or to stand up another server
/// like it. Like the template, it never holds a password: the installer never reads one from it. A value this server
/// can't know is left blank with a note, and the installer asks for the required ones.
/// </summary>
public static class InstallAnswers
{
    public const string FileName = "tide.config.psd1";

    public static string Write(InstallFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TIDE install answers file, " + f.Origin + ".");
        sb.AppendLine("# Check every value, then pass it to the installers:");
        sb.AppendLine("#   .\\Install-Database.ps1 -ConfigFile .\\tide.config.psd1");
        sb.AppendLine("#   .\\Install-Tide.ps1     -ConfigFile .\\tide.config.psd1");
        sb.AppendLine("# A parameter passed on the command line wins over this file. No password ever goes in here.");
        sb.AppendLine("@{");
        sb.AppendLine("    # --- SQL Server ---");
        Line(sb, "SqlInstance", f.SqlInstance, "the SQL Server instance");
        Line(sb, "DbName", f.DbName, "the database");
        Line(sb, "AppAccount", f.AppAccount, "the app pool's account (a gMSA ends in '$')");
        Line(sb, "SchemaMode", f.AppMigrates ? "AppMigrates" : "DbaApplies");
        Line(sb, "DataPath", "");
        Line(sb, "LogPath", "");
        Line(sb, "ValidateServerCertificate", f.ValidateServerCertificate);
        sb.AppendLine();
        sb.AppendLine("    # --- IIS ---");
        if (f.AppPath is { } appPath)
        {
            sb.AppendLine("    # This server runs TIDE as an application under another site, at /" + Escape(appPath) + ".");
            Line(sb, "SiteName", "TIDE");
        }
        else
        {
            Line(sb, "SiteName", null, "the IIS site's name");
        }
        Line(sb, "AppPoolName", f.AppPoolName, "the app pool's name");
        Line(sb, "Hostname", f.Hostname, "the host name readers use");
        Line(sb, "SitePath", f.SitePath, "the site folder");
        Line(sb, "DataRoot", f.DataRoot, "the data folder, outside the web root");
        Line(sb, "CertificateThumbprint", null, "the HTTPS certificate's thumbprint (not needed under another site)");
        Line(sb, "ParentSite", f.AppPath is null ? "" : null, f.AppPath is null ? null : "the site TIDE is an application under");
        Line(sb, "AppPath", f.AppPath ?? "");
        // Not read from this server: whether the installer may install a Windows feature is the administrator's call, and
        // a server whose pages already use WebSockets has the feature, so the value changes nothing there. The comment is
        // the template's, word for word (InstallAnswersTemplateTests holds them equal).
        sb.AppendLine("    # $true to let Install-Tide.ps1 install the IIS WebSocket Protocol feature when it's missing (Web-WebSockets; TIDE's");
        sb.AppendLine("    # pages fall back to long polling, a request per keystroke, without it). $false: a run from this file warns instead,");
        sb.AppendLine("    # says how to install it, and carries on.");
        Line(sb, "InstallWebSockets", false);
        sb.AppendLine();
        sb.AppendLine("    # --- Who can do what (one group per role; the others mapped here are listed after it) ---");
        Group(sb, "AdGroupAnalysts", f, AppRole.Analyst);
        Group(sb, "AdGroupActionOwners", f, AppRole.ActionOwner);
        Group(sb, "AdGroupReaders", f, AppRole.Reader);
        Group(sb, "AdGroupAdmins", f, AppRole.Admin);
        sb.AppendLine();
        sb.AppendLine("    # --- The organization ---");
        Line(sb, "TimeZone", f.TimeZone, "an IANA time zone");
        Line(sb, "MeetingTime", f.MeetingTime, "when the brief meets, as 10:30");
        Line(sb, "TeamName", f.TeamName, "the team's name");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void Group(StringBuilder sb, string name, InstallFacts f, AppRole role)
    {
        var groups = f.Groups.TryGetValue(role, out var g) ? g : [];
        var note = groups.Count > 1 ? "also mapped in the app: " + string.Join(", ", groups.Skip(1)) : null;
        Line(sb, name, groups.Count > 0 ? groups[0] : "", note, known: true);
    }

    private static void Line(StringBuilder sb, string name, bool value) =>
        sb.Append("    ").Append(name.PadRight(21)).Append(" = ").AppendLine(value ? "$true" : "$false");

    private static void Line(StringBuilder sb, string name, string? value, string? fillIn = null, bool known = false)
    {
        sb.Append("    ").Append(name.PadRight(21)).Append(" = '").Append(Escape(value ?? "")).Append('\'');
        if (value is null && fillIn is not null) sb.Append("   # not known on this server: ").Append(fillIn);
        else if (known && fillIn is not null) sb.Append("   # ").Append(Escape(fillIn));
        sb.AppendLine();
    }

    // A single-quoted PowerShell string takes everything literally except a quote, which is doubled. PowerShell counts the
    // typographic single quotes (‘ ’ ‚ ‛) as quotes too, so "Robin’s team" would end the string: they're doubled as well.
    // Line breaks can't be in a value here, so they're dropped.
    internal static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c == '\r') continue;
            if (c == '\n') { sb.Append(' '); continue; }
            if (c is '\'' or (char)0x2018 or (char)0x2019 or (char)0x201A or (char)0x201B) sb.Append(c);
            sb.Append(c);
        }
        return sb.ToString();
    }
}
