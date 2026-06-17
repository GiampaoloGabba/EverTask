using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Storage.EfCore;

/// <summary>
/// Carries the EverTask schema name as part of the (immutable, shared) <see cref="DbContextOptions"/> so a
/// pool-compatible <see cref="TaskStoreEfDbContext{T}"/> can read it WITHOUT a constructor dependency.
/// <para>
/// <see cref="M:Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddPooledDbContextFactory"/>
/// requires the context to expose a SINGLE <c>DbContextOptions&lt;T&gt;</c> constructor, which rules out the
/// previous <c>IOptions&lt;ITaskStoreOptions&gt;</c> ctor parameter. The schema therefore travels inside the
/// options instead — options are part of EF's immutable, shared configuration, so this is pool-safe.
/// </para>
/// </summary>
public sealed class EverTaskSchemaExtension(string? schema) : IDbContextOptionsExtension
{
    public string? Schema { get; } = schema;

    private DbContextOptionsExtensionInfo? _info;
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    // No EF services to register: the schema is consumed only by the context ctor / OnModelCreating.
    public void ApplyServices(IServiceCollection services) { }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(EverTaskSchemaExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new EverTaskSchemaExtension Extension => (EverTaskSchemaExtension)base.Extension;

        // This extension only changes configuration (the default schema), it is not a database provider.
        public override bool IsDatabaseProvider => false;

        // The schema feeds OnModelCreating (HasDefaultSchema), which means it changes the built model.
        // It MUST take part in EF's internal service-provider / model cache key, otherwise two contexts
        // configured with different schemas could share the same cached model, or EF would rebuild the
        // internal service provider per context (silently destroying the pooling win). Fold the schema in.
        public override int GetServiceProviderHashCode()
            => Extension.Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(Extension.Schema);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info
               && string.Equals(info.Extension.Schema, Extension.Schema, StringComparison.Ordinal);

        public override string LogFragment
            => string.IsNullOrEmpty(Extension.Schema) ? "" : $"using EverTask schema '{Extension.Schema}' ";

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["EverTask:Schema"] = Extension.Schema ?? "";
    }
}

public static class EverTaskSchemaDbContextOptionsExtensions
{
    /// <summary>
    /// Routes the EverTask schema name through the DbContext options so a pooled, single-ctor context can
    /// read it via <c>options.FindExtension&lt;EverTaskSchemaExtension&gt;()</c>. Call this alongside
    /// <c>UseSqlite</c>/<c>UseSqlServer</c>/<c>UseNpgsql</c> when registering the pooled factory.
    /// </summary>
    public static DbContextOptionsBuilder UseEverTaskSchema(this DbContextOptionsBuilder optionsBuilder, string? schema)
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new EverTaskSchemaExtension(schema));
        return optionsBuilder;
    }
}
