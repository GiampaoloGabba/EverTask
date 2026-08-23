using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;

namespace EverTask.Tests.Storage.EfCore;

/// <summary>
/// Pins the SQL a migration actually emits, per provider, against a committed snapshot.
/// </summary>
/// <remarks>
/// Migrations are frozen once released: the model snapshot alone does not notice a hand-edited procedure
/// body, a dropped index filter or a changed <c>ON DELETE</c> rule, because none of that is part of the
/// model. The emitted script is, and it is also the only artefact that shows the four providers really
/// agreeing on the same schema. A missing snapshot is written on the spot and the test fails, so the new
/// baseline is reviewed in the diff instead of appearing silently.
/// <para>
/// Callers guard themselves to ONE target framework (<c>#if NET10_0</c>). EF Core renders the same migration
/// differently across its own major versions — batch separators, where the transaction is committed — and the
/// three TFMs of this repository pull three different EF Core majors, so pinning all of them would be pinning
/// EF's renderer rather than the migration. The migration files are shared, so one leg catches any change to
/// them; it also keeps three parallel test hosts from writing the same snapshot file at once. A new TFM needs
/// a decision here, not an extra <c>#if</c>.
/// </para>
/// </remarks>
internal static class MigrationSqlSnapshot
{
    public static void Verify(DbContext context, string fromMigrationId, string toMigrationId,
                              string snapshotName, [CallerFilePath] string callerFile = "")
    {
        var script     = context.GetService<IMigrator>().GenerateScript(fromMigrationId, toMigrationId);
        var normalized = Normalize(script);

        var directory = Path.Combine(Path.GetDirectoryName(callerFile)!, "MigrationSnapshots");
        var path      = Path.Combine(directory, snapshotName + ".sql");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, normalized);
            throw new ShouldAssertException(
                $"No migration SQL snapshot for '{snapshotName}'. One has been written to {path}: " +
                "review it and commit it as the baseline.");
        }

        var expected = Normalize(File.ReadAllText(path));

        if (expected == normalized)
            return;

        var actualPath = Path.Combine(directory, snapshotName + ".actual.sql");
        File.WriteAllText(actualPath, normalized);

        normalized.ShouldBe(expected,
            $"The SQL emitted by '{toMigrationId}' changed. A released migration is frozen: if the change is " +
            $"intentional it belongs in a NEW migration. What was emitted is in {actualPath}.");
    }

    /// <summary>
    /// Line endings, trailing spaces and the EF Core build stamped into <c>__EFMigrationsHistory</c> are not
    /// part of what the migration does — the last one changes with every package bump.
    /// </summary>
    private static string Normalize(string script)
    {
        var withoutVersion = script.Replace(ProductInfo.GetVersion(), "<EFCORE-VERSION>", StringComparison.Ordinal);

        var lines = withoutVersion.Replace("\r\n", "\n", StringComparison.Ordinal)
                                  .Split('\n')
                                  .Select(l => l.TrimEnd());

        return Regex.Replace(string.Join('\n', lines).Trim(), "\n{3,}", "\n\n");
    }
}
