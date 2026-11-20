using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Content;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Makes SQLite lower-case and order text as SQL Server's collation does. SQLite's own lower() and NOCASE fold A to Z
/// only, so "Égout" stayed apart from "éclair", and its byte order put both after "Zebra". On SQLite the context gives
/// every connection a lower() that folds every letter and a <see cref="Collation"/> that orders by
/// <see cref="NameOrder.Compare"/>, and translates <c>ToLower()</c> to <c>lower(x) COLLATE unicode_ci</c>, so every
/// case-insensitive sort (<see cref="NameOrder"/>, the title and team columns) agrees with SQL Server with no query
/// written for one provider. SQL Server is left alone: its collation already does this.
/// </summary>
/// <remarks>
/// Only queries use these; nothing in the schema does, so the database file still opens in any SQLite tool. A sort runs
/// the comparison in .NET once per pair of rows compared, which at a team's list sizes is a few milliseconds.
/// </remarks>
internal static class SqliteText
{
    public const string Collation = "unicode_ci";

    private static readonly Assembly SqliteProvider = typeof(SqliteDbContextOptionsBuilder).Assembly;

    // The connections a caller opened and handed to the context, each given the functions once.
    private static readonly ConditionalWeakTable<SqliteConnection, object> Given = [];

    /// <summary>Gives SQLite contexts the functions and translation; any other provider's options are left as they are.</summary>
    public static void Configure(DbContextOptionsBuilder options)
    {
        if (!options.Options.Extensions.Any(e => e.Info.IsDatabaseProvider && e.GetType().Assembly == SqliteProvider)) return;
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new Extension());
        // A connection the context makes from a connection string gets them as it's made; one handed in, now.
        options.AddInterceptors(Connections.Instance);
        if (RelationalOptionsExtension.Extract(options.Options).Connection is SqliteConnection given)
            Given.GetValue(given, c => { Register((SqliteConnection)c); return Given; });
    }

    /// <summary>
    /// Adds the functions to a connection. Microsoft.Data.Sqlite keeps them and adds them again each time it opens, so a
    /// closed connection can be given them too.
    /// </summary>
    public static void Register(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string?>("lower", s => s?.ToLowerInvariant(), isDeterministic: true);
        connection.CreateCollation(Collation, NameOrder.Compare);
    }

    private sealed class Connections : DbConnectionInterceptor
    {
        public static readonly Connections Instance = new();

        public override DbConnection ConnectionCreated(ConnectionCreatedEventData eventData, DbConnection result)
        {
            if (result is SqliteConnection sqlite) Register(sqlite);
            return result;
        }
    }

    /// <summary><c>ToLower()</c> as <c>lower(x) COLLATE unicode_ci</c>, ahead of SQLite's own <c>lower(x)</c>.</summary>
    private sealed class ToLowerTranslator(ISqlExpressionFactory sql) : IMethodCallTranslator
    {
        private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

        public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (instance is null || !method.Equals(ToLower)) return null;
            var lowered = sql.Function("lower", [instance], nullable: true, argumentsPropagateNullability: [true], method.ReturnType,
                instance.TypeMapping);
            return new CollateExpression(lowered, Collation);
        }
    }

    private sealed class TranslatorPlugin(ISqlExpressionFactory sql) : IMethodCallTranslatorPlugin
    {
        public IEnumerable<IMethodCallTranslator> Translators { get; } = [new ToLowerTranslator(sql)];
    }

    private sealed class Extension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services) =>
            new EntityFrameworkRelationalServicesBuilder(services).TryAdd<IMethodCallTranslatorPlugin, TranslatorPlugin>();

        public void Validate(IDbContextOptions options) { }

        private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "SqliteText ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["SqliteText"] = "1";
        }
    }
}
