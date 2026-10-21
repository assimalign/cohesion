using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlDeclaredDatabase
{
    private IReadOnlyList<SqlDeclaredScript> _scripts = [];
    private string _scriptHash = string.Empty;

    private static void ValidateScriptOwnership(SqlCompiledSchema? schema, IReadOnlyList<SqlDeclaredScript> scripts)
    {
        if (schema is null) { return; }
        foreach (var script in scripts)
        {
            foreach (var request in script.Statements)
            {
                if (request.Statement.SqlExpression is SqlDropSchemaExpression { Cascade: true } dropSchema &&
                    dropSchema.Name.Equals(SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase) && schema.Tables.Count != 0)
                {
                    throw new SqlSchemaMigrationException(string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.ScriptNamespaceTypedObjects, script.Name, dropSchema.Name));
                }
                SqlTableReference? target = request.Statement.SqlExpression switch
                {
                    SqlAlterTableExpression alter => alter.Table,
                    SqlDropTableExpression drop => drop.Table,
                    SqlDropIndexExpression drop => drop.Table,
                    _ => null,
                };
                if (target is null || !(target.SchemaName ?? SqlPlanner.DefaultSchema).Equals(SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase)) { continue; }
                var table = schema.Tables.FirstOrDefault(table => table.Name.Equals(target.TableName, StringComparison.OrdinalIgnoreCase));
                if (table is null) { continue; }
                string objectName = SqlPlanner.DefaultSchema + "." + table.Name;
                if (request.Statement.SqlExpression is SqlDropIndexExpression index)
                {
                    if (!table.Indexes.Any(declared => declared.Name.Equals(index.IndexName, StringComparison.OrdinalIgnoreCase)) &&
                        !string.Equals(table.PrimaryKey?.Name, index.IndexName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    objectName += "." + index.IndexName;
                }
                throw new SqlSchemaMigrationException($"COHSQLS005: Declared script '{script.Name}' cannot alter or drop typed schema object '{objectName}'; declare the change in the typed schema. Nothing was opened or written.");
            }
        }
    }

    private bool ScriptsApplied(SqlDatabase database)
        => database.Catalog.ScriptState is { } state && state.ContentHash == _scriptHash && state.Generation == database.Catalog.DdlGeneration;

    private async ValueTask ValidateScriptsAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        if (_scripts.Count == 0 || ScriptsApplied(database)) { return; }
        var receipts = database.Catalog.Scripts.ToDictionary(receipt => receipt.Name, StringComparer.Ordinal);
        foreach (var script in _scripts)
        {
            receipts.TryGetValue(script.Name, out var receipt);
            if (receipt is not null && receipt.ContentHash != script.ContentHash)
            {
                throw new SqlSchemaMigrationException($"COHSQLS001: Declared script '{script.Name}' has a different body from its recorded SHA-256. Use a new name for forward-only changes.");
            }
            if (receipt is not null)
            {
                if (receipt.StatementCount != script.Statements.Count || receipt.StatementObjects.Count != receipt.CompletedStatements)
                {
                    throw new SqlSchemaMigrationException($"COHSQLS001: Declared script '{script.Name}' has an inconsistent recorded statement count or target list.");
                }
                var expected = new string[receipt.CompletedStatements];
                for (int position = 0; position < receipt.CompletedStatements; position++)
                {
                    expected[position] = ScriptTarget(database, script, position, receipt);
                    if (!string.Equals(receipt.StatementObjects[position], expected[position], StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS001: Declared script '{script.Name}', statement {position + 1} targets '{expected[position]}' but its receipt records '{receipt.StatementObjects[position]}'.");
                    }
                }
                var created = Enumerable.Range(0, receipt.CompletedStatements)
                    .Where(position => script.Statements[position].Statement.SqlExpression is SqlCreateTableExpression or SqlCreateIndexExpression or SqlCreateSchemaExpression or SqlCreateViewExpression)
                    .Select(position => expected[position]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!created.SetEquals(receipt.Objects))
                {
                    throw new SqlSchemaMigrationException($"COHSQLS001: Declared script '{script.Name}' records object ownership inconsistent with its completed CREATE statements.");
                }
            }
            if (Mode == SqlProvisioningMode.Verify && (receipt is null || receipt.CompletedStatements != script.Statements.Count))
            {
                throw new SqlSchemaMigrationException($"COHSQLS002: Declared script '{script.Name}' is not completely recorded with its checksum.");
            }
        }
        if (Mode == SqlProvisioningMode.Verify) { return; }
        // An interrupted script can advance metadata after the typed-schema marker. Only a
        // continuous chain of receipts from that marker proves that no unrelated DDL intervened.
        if (database.Catalog.SchemaState is { } typed && typed.Generation != database.Catalog.DdlGeneration)
        {
            ulong generation = typed.Generation;
            bool advanced;
            do
            {
                advanced = false;
                foreach (var receipt in receipts.Values)
                {
                    if (receipt.SchemaHash == typed.ContentHash && receipt.BaseGeneration <= generation && receipt.Generation > generation)
                    {
                        generation = receipt.Generation;
                        advanced = true;
                    }
                }
            } while (advanced);
            if (generation == database.Catalog.DdlGeneration)
            {
                await database.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(typed.ContentHash, typed.CanonicalDocument, generation), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ApplyScriptsAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        if (_scripts.Count == 0 || ScriptsApplied(database) || Mode == SqlProvisioningMode.Verify) { return; }
        using var admission = database.DdlInterlock.EnterApply(Name);
        await using var session = database.CreateSchemaSession(Name, cancellationToken);
        var dataReceipts = await SqlScriptDataReceipt.ReadAsync(database, cancellationToken).ConfigureAwait(false);
        var receipts = database.Catalog.Scripts.ToDictionary(receipt => receipt.Name, StringComparer.Ordinal);
        foreach (var script in _scripts)
        {
            receipts.TryGetValue(script.Name, out var existing);
            int completed = existing?.CompletedStatements ?? 0;
            // Persist the checksum before any effects, including a first DML transaction.
            if (existing is null)
            {
                existing = await database.Catalog.BeginScriptAsync(script.Name, script.ContentHash, script.Statements.Count,
                    Schema?.Hash, cancellationToken).ConfigureAwait(false);
            }
            for (int position = completed; position < script.Statements.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var receipt = new SqlCatalogScriptPublication(script.Name, script.ContentHash, script.Statements.Count,
                    existing, database.Catalog.DdlGeneration, existing!.SchemaHash, ScriptTarget(database, script, position, existing));
                if (!dataReceipts.Contains((receipt.Name, receipt.ContentHash, receipt.CompletedStatements)))
                {
                    if (script.Statements[position].Statement.SqlExpression is SqlCreateTableExpression create &&
                        database.Catalog.TryGetTable(create.Table.SchemaName ?? SqlPlanner.DefaultSchema, create.Table.TableName, out _))
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}' cannot adopt existing table '{create.Table.TableName}'.");
                    }
                    if (script.Statements[position].Statement.SqlExpression is SqlCreateIndexExpression index &&
                        database.Catalog.TryGetTable(index.Table.SchemaName ?? SqlPlanner.DefaultSchema, index.Table.TableName, out var indexedTable) &&
                        database.Catalog.GetIndexes(indexedTable.ObjectId).Any(existingIndex => existingIndex.Name.Equals(index.IndexName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}' cannot adopt existing index '{index.IndexName}'.");
                    }
                    var result = await session.ExecuteScriptAsync(script.Statements[position], receipt, cancellationToken).ConfigureAwait(false);
                    if (result is DatabaseResultSet rows) { await rows.DisposeAsync().ConfigureAwait(false); }
                    if (result.Status != DatabaseResultStatus.Success)
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}', statement {position + 1} failed.");
                    }
                }
                if (receipt.ExpectedObject.Length == 0)
                {
                    await database.Catalog.CompleteScriptStatementAsync(receipt, cancellationToken).ConfigureAwait(false);
                }
                existing = database.Catalog.Scripts.Single(recorded => recorded.Name == script.Name);
                if (existing.CompletedStatements != position + 1)
                {
                    throw new SqlSchemaMigrationException($"COHSQLS001: Script '{script.Name}', statement {position + 1} did not publish its expected receipt.");
                }
            }
            if (completed != script.Statements.Count)
            {
                SqlDatabaseEventSource.Log.ScriptApplied(database, script.Name, script.ContentHash);
            }
        }
        if (Schema is not null)
        {
            await database.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(Schema.Hash, Schema.CanonicalDocument, database.Catalog.DdlGeneration), cancellationToken).ConfigureAwait(false);
        }
        await database.Catalog.SaveScriptStateAsync(new SqlCatalogScriptState(_scriptHash, database.Catalog.DdlGeneration), cancellationToken).ConfigureAwait(false);
    }

    private static string ScriptTarget(SqlDatabase database, SqlDeclaredScript script, int position, SqlCatalogScript receipt)
    {
        bool recorded = position < receipt.CompletedStatements;
        var expression = script.Statements[position].Statement.SqlExpression;
        if (expression is SqlAlterTableExpression { Action: SqlAlterDropConstraintAction drop } alter)
        {
            string index = SqlObjectIdentity.Index(alter.Table.SchemaName ?? SqlPlanner.DefaultSchema, alter.Table.TableName, drop.ConstraintName);
            // UNIQUE is represented by an index. For completed statements either this exact
            // index or the containing table (CHECK/FK) is a legitimate DROP CONSTRAINT target.
            if (recorded ? string.Equals(receipt.StatementObjects[position], index, StringComparison.OrdinalIgnoreCase)
                : database.Catalog.TryGetTable(alter.Table.SchemaName ?? SqlPlanner.DefaultSchema, alter.Table.TableName, out var table) &&
                  !table.Constraints.Any(constraint => constraint.Name.Equals(drop.ConstraintName, StringComparison.OrdinalIgnoreCase)) &&
                  database.Catalog.TryGetIndex(table.ObjectId, drop.ConstraintName, out var unique) && unique.IsUnique)
            {
                return index;
            }
        }
        return script.ObjectKey(position, Resolve);

        SqlType Resolve(string name, int parameter)
        {
            if (SqlTypeCatalog.Standard.Resolve(name, out string? builtinError) is { } builtin && builtinError is null) { return builtin; }
            // A domain may have been dropped and its CREATE script removed from code. The
            // catalog captured CREATE's definition or DROP's resolved requested parameter names,
            // paired with the actual mutation's storage signature, so historical identity needs
            // no live type registration.
            if (recorded && parameter < receipt.StatementParameterTypes[position].Count &&
                SqlTypeRules.TrySplit(name, out _, out int? first, out int? second) && first is null && second is null &&
                SqlTypeRules.SameTypeName(receipt.StatementParameterTypes[position][parameter], name))
            {
                string target = receipt.StatementObjects[position];
                int opening = target.LastIndexOf('(');
                if (opening >= 0 && target.EndsWith(')'))
                {
                    string[] types = target[(opening + 1)..^1].Split(',');
                    if (parameter < types.Length && int.TryParse(types[parameter], NumberStyles.None, CultureInfo.InvariantCulture, out int storage) &&
                        storage > (int)DatabaseType.Null && storage <= (int)DatabaseType.Character)
                    {
                        return SqlType.ForStorage(new DatabaseTypeInfo((DatabaseType)storage));
                    }
                }
            }
            if (database.Definitions.Functions.Types.Resolve(name, out string? error) is { } type && error is null) { return type; }
            throw new SqlSchemaMigrationException($"COHSQLS001: Script '{script.Name}', statement {position + 1}: cannot resolve type '{name}' while validating its function target.");
        }
    }
}
