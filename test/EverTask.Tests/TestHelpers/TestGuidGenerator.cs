using EverTask.Abstractions;
using UUIDNext;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Generates database-optimized, time-ordered GUIDs (v7/v8) for tests.
/// Ensures deterministic ordering for keyset pagination validation.
/// </summary>
/// <remarks>
/// GUID v7 are time-ordered, but sorting behavior differs between:
/// - .NET Guid.CompareTo() (proprietary Microsoft algorithm)
/// - SQL Server uniqueidentifier (proprietary sorting)
/// - SQLite BLOB/TEXT (byte-by-byte lexicographic)
///
/// This helper uses DefaultGuidGenerator with database-specific optimizations
/// to ensure consistent ordering for each provider.
/// </remarks>
public static class TestGuidGenerator
{
    private static readonly IGuidGenerator _defaultGenerator =
        new DefaultGuidGenerator(Database.Other);

    private static readonly IGuidGenerator _sqlServerGenerator =
        new DefaultGuidGenerator(Database.SqlServer);

    private static readonly IGuidGenerator _sqliteGenerator =
        new DefaultGuidGenerator(Database.SQLite);

    /// <summary>
    /// Generates GUID v7 optimized for generic databases.
    /// Use in unit tests and memory storage tests.
    /// </summary>
    public static Guid New() => _defaultGenerator.NewDatabaseFriendly();

    /// <summary>
    /// Generates GUID v7 optimized for SQL Server uniqueidentifier sorting.
    /// Use in SQL Server storage tests.
    /// </summary>
    public static Guid NewForSqlServer() => _sqlServerGenerator.NewDatabaseFriendly();

    /// <summary>
    /// Generates GUID v7 optimized for SQLite BLOB/TEXT sorting.
    /// Use in SQLite storage tests.
    /// </summary>
    public static Guid NewForSqlite() => _sqliteGenerator.NewDatabaseFriendly();
}
