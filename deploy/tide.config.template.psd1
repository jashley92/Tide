# TIDE install answers file. Copy to tide.config.psd1 (git-ignored: it names your AD groups, hosts and accounts), fill
# it in, and pass it to both installers:
#   .\Install-Database.ps1 -ConfigFile .\tide.config.psd1
#   .\Install-Tide.ps1     -ConfigFile .\tide.config.psd1
# A parameter passed on the command line wins over this file. No password ever goes in here.
@{
    # --- SQL Server (Install-Database.ps1 and Install-Tide.ps1) ---
    # The instance TIDE shares with your other apps; TIDE gets a database of its own.
    SqlInstance = 'SQLHOST\INSTANCE'
    DbName      = 'Tide'
    # Windows account the TIDE app pool runs as. A gMSA ends in '$'. Must exist in AD.
    AppAccount  = 'CONTOSO\svc-tide$'
    # AppMigrates: the app applies its schema at startup (db_owner). DbaApplies: a DBA runs the schema script.
    SchemaMode  = 'AppMigrates'
    DataPath    = ''
    LogPath     = ''
    # OPTIONAL: the account the backup tasks run as (a gMSA, or for tasks run as SYSTEM this server's computer account,
    # CONTOSO\WEB01$). Install-Database.ps1 gives it db_backupoperator, dbcreator and SELECT on msdb.dbo.backupset.
    BackupAccount = ''
    # $true to check the SQL Server's TLS certificate (it must chain to a trusted root and name SqlInstance's host).
    # Off by default, as most instances have a self-signed one: the connection is encrypted, but who it's with isn't checked.
    ValidateServerCertificate = $false

    # --- IIS (Install-Tide.ps1) ---
    # A site of its own beside the server's other sites: its own host name, pool, web root and data root.
    SiteName    = 'TIDE'
    AppPoolName = 'TIDE'
    Hostname    = 'tide.contoso.com'
    SitePath    = 'D:\inetpub\tide'
    DataRoot    = 'E:\TideData'
    # An installed certificate's thumbprint for HTTPS on Hostname:443 (bound with SNI, so other sites keep theirs).
    CertificateThumbprint = ''
    # OPTIONAL, instead of a site of its own: install TIDE as an application under an existing site, at
    # https://<that site's host>/<AppPath>, still in its own pool and folder (SitePath, outside that site's folder).
    # SiteName and CertificateThumbprint are then unused, and Hostname defaults to the site's own. Leave both blank for
    # the usual site of its own. See docs/INSTALL.md, "Under another site".
    ParentSite  = ''
    AppPath     = ''
    # $true to let Install-Tide.ps1 install the IIS WebSocket Protocol feature when it's missing (Web-WebSockets; TIDE's
    # pages fall back to long polling, a request per keystroke, without it). $false: a run from this file warns instead,
    # says how to install it, and carries on.
    InstallWebSockets = $false

    # --- Who can do what: AD groups for each TIDE role (DOMAIN\Group). Leave one blank to map it later in the app. ---
    AdGroupAnalysts     = 'CONTOSO\CTI-Analysts'
    AdGroupActionOwners = 'CONTOSO\Security-Teams'
    AdGroupReaders      = 'CONTOSO\Domain Users'
    AdGroupAdmins       = 'CONTOSO\CTI-Admins'

    # --- The organization ---
    TimeZone    = 'America/New_York'
    MeetingTime = '10:30'
    TeamName    = 'Cyber Threat Intelligence'
}
