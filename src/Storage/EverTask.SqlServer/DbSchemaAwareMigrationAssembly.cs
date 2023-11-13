using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using EverTask.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace EverTask.SqlServer;

[SuppressMessage("Usage", "EF1001:Internal EF Core API usage.")]
public class DbSchemaAwareMigrationAssembly(
    ICurrentDbContext currentContext,
    IDbContextOptions options,
    IMigrationsIdGenerator idGenerator,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger) : MigrationsAssembly(currentContext, options, idGenerator, logger)
{
    private readonly DbContext _context = currentContext.Context;

    public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        ArgumentNullException.ThrowIfNull(activeProvider);

        var hasCtorWithSchema = migrationClass.GetConstructor(new[] { typeof(ITaskStoreDbContext) }) is not null;

        if (!hasCtorWithSchema || _context is not ITaskStoreDbContext schema)
            return base.CreateMigration(migrationClass, activeProvider);

        var instance = (Migration)Activator.CreateInstance(migrationClass.AsType(), schema)!;
        instance.ActiveProvider = activeProvider;
        return instance;
    }
}
