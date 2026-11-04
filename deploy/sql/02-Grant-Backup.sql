/*
    TIDE: what the account that runs Backup-Tide.ps1 and Test-TideRestore.ps1 needs on SQL Server, and no more:
      - db_backupoperator in TIDE's database (BACKUP DATABASE and BACKUP LOG);
      - dbcreator on the server (RESTORE VERIFYONLY, which checks each set, and the restore drill need CREATE DATABASE);
      - SELECT on msdb.dbo.backupset (the size of each set is read from it).
    Run by Install-Database.ps1 -BackupAccount, or by hand with sqlcmd:

        sqlcmd -S SQLHOST -E -b -i 02-Grant-Backup.sql ^
               -v DbName="Tide" BackupAccount="CONTOSO\svc-tide-bk$"

    BackupAccount  The Windows account the backup tasks run as: a gMSA (with its trailing '$'), or, for tasks that run
                   as SYSTEM, the web server's computer account (CONTOSO\WEB01$). Idempotent: safe to re-run.
*/
:on error exit
SET NOCOUNT ON;

IF N'$(BackupAccount)' = N'' OR N'$(BackupAccount)' = N'$' + N'(BackupAccount)'
BEGIN
    RAISERROR('BackupAccount variable is required (e.g. -v BackupAccount="CONTOSO\svc-tide-bk$").', 16, 1);
    RETURN;
END

IF SUSER_ID(N'$(BackupAccount)') IS NULL
BEGIN
    CREATE LOGIN [$(BackupAccount)] FROM WINDOWS;
    PRINT 'Created login [$(BackupAccount)].';
END

IF IS_SRVROLEMEMBER('dbcreator', N'$(BackupAccount)') <> 1
BEGIN
    ALTER SERVER ROLE dbcreator ADD MEMBER [$(BackupAccount)];
    PRINT 'Added [$(BackupAccount)] to dbcreator (RESTORE VERIFYONLY and the restore drill).';
END
GO

USE [$(DbName)];
IF USER_ID(N'$(BackupAccount)') IS NULL
BEGIN
    CREATE USER [$(BackupAccount)] FOR LOGIN [$(BackupAccount)];
    PRINT 'Created database user [$(BackupAccount)] in [$(DbName)].';
END
ALTER ROLE db_backupoperator ADD MEMBER [$(BackupAccount)];
PRINT 'Granted db_backupoperator in [$(DbName)] to [$(BackupAccount)].';
GO

USE msdb;
IF USER_ID(N'$(BackupAccount)') IS NULL
    CREATE USER [$(BackupAccount)] FOR LOGIN [$(BackupAccount)];
GRANT SELECT ON dbo.backupset TO [$(BackupAccount)];
PRINT 'Granted SELECT on msdb.dbo.backupset to [$(BackupAccount)].';
GO
