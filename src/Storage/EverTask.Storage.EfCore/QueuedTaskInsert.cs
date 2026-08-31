using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EverTask.Storage.EfCore;

internal sealed class QueuedTaskInsert
{
    private readonly IClrPropertyGetter[]    _getters;
    private readonly RelationalTypeMapping[] _mappings;
    private readonly string[]                _parameterNames;
    private readonly bool[]                  _nullable;

    internal string Sql { get; }

    internal QueuedTaskInsert(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(QueuedTask))
                         ?? throw new QueuedTaskInsertUnavailableException(
                             "QueuedTask is not mapped in the EF model");

        if (entityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned()))
            throw new QueuedTaskInsertUnavailableException(
                "QueuedTask has an owned navigation that Persist cannot insert");

        var tableName = entityType.GetTableName()
                        ?? throw new QueuedTaskInsertUnavailableException("QueuedTask has no mapped table");
        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        var properties = entityType.GetProperties().ToArray();
        var helper = context.GetService<ISqlGenerationHelper>();
        var columns = new string[properties.Length];

        _getters        = new IClrPropertyGetter[properties.Length];
        _mappings       = new RelationalTypeMapping[properties.Length];
        _parameterNames = new string[properties.Length];
        _nullable       = new bool[properties.Length];

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (property.IsShadowProperty())
                throw new QueuedTaskInsertUnavailableException(
                    $"QueuedTask property '{property.Name}' is shadow-mapped");
            if (property.GetBeforeSaveBehavior() != PropertySaveBehavior.Save)
                throw new QueuedTaskInsertUnavailableException(
                    $"QueuedTask property '{property.Name}' is store-generated");

            var columnName = property.GetColumnName(table)
                             ?? throw new QueuedTaskInsertUnavailableException(
                                 $"QueuedTask property '{property.Name}' is not mapped to '{tableName}'");

            columns[i]         = helper.DelimitIdentifier(columnName);
            _getters[i]        = property.GetGetter();
            _mappings[i]       = property.GetRelationalTypeMapping();
            _parameterNames[i] = "@p" + i.ToString(CultureInfo.InvariantCulture);
            _nullable[i]       = property.IsNullable;
        }

        Sql = "INSERT INTO " + helper.DelimitIdentifier(tableName, entityType.GetSchema())
                             + " (" + string.Join(", ", columns) + ") VALUES ("
                             + string.Join(", ", _parameterNames) + ")";
    }

    internal DbParameter[] Parameters(DbContext context, QueuedTask task)
    {
        // The mapping uses the provider command only to create typed parameters; EF executes the INSERT.
        using var command = context.Database.GetDbConnection().CreateCommand();
        var parameters = new DbParameter[_getters.Length];

        for (var i = 0; i < parameters.Length; i++)
            parameters[i] = _mappings[i].CreateParameter(
                command, _parameterNames[i], _getters[i].GetClrValue(task), _nullable[i]);

        return parameters;
    }
}

internal sealed class QueuedTaskInsertUnavailableException(string message) : InvalidOperationException(message);
