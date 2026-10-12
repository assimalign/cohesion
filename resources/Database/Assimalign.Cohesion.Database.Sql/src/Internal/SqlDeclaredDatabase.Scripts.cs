using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlDeclaredDatabase
{
    private IReadOnlyList<SqlDeclaredScript> _scripts = [];
    private string _scriptHash = string.Empty;

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
            ulong beginning = existing?.BaseGeneration ?? database.Catalog.DdlGeneration;
            int completed = existing?.CompletedStatements ?? 0;
            // Persist the checksum before any effects, including a first DML transaction.
            if (existing is null)
            {
                await database.Catalog.SaveScriptAsync(new SqlCatalogScript(script.Name, script.ContentHash, script.Statements.Count,
                    0, beginning, beginning, Schema?.Hash, []), cancellationToken).ConfigureAwait(false);
            }
            for (int position = completed; position < script.Statements.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var receipt = new SqlCatalogScript(script.Name, script.ContentHash, script.Statements.Count,
                    position + 1, beginning, database.Catalog.DdlGeneration, Schema?.Hash, script.CreatedObjects(position + 1));
                if (!dataReceipts.Contains((receipt.Name, receipt.ContentHash, receipt.CompletedStatements)))
                {
                    if (script.Statements[position].Statement.SqlExpression is SqlCreateTableExpression create &&
                        database.Catalog.TryGetTable(create.Table.SchemaName ?? "dbo", create.Table.TableName, out _))
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}' cannot adopt existing table '{create.Table.TableName}'.");
                    }
                    if (script.Statements[position].Statement.SqlExpression is SqlCreateIndexExpression index &&
                        database.Catalog.TryGetTable(index.Table.SchemaName ?? "dbo", index.Table.TableName, out var indexedTable) &&
                        database.Catalog.GetIndexes(indexedTable.ObjectId).Any(existingIndex => existingIndex.Name.Equals(index.IndexName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}' cannot adopt existing index '{index.IndexName}'.");
                    }
                    using var enlistment = database.Catalog.BeginScriptStatement(receipt);
                    session.ScriptReceipt = receipt;
                    var result = await session.ExecuteAsync(script.Statements[position], cancellationToken).ConfigureAwait(false);
                    if (result is QueryResultSet rows) { await rows.DisposeAsync().ConfigureAwait(false); }
                    if (result.Status != QueryResultStatus.Success)
                    {
                        throw new SqlSchemaMigrationException($"COHSQLS004: Script '{script.Name}', statement {position + 1} failed.");
                    }
                }
                await database.Catalog.SaveScriptAsync(new SqlCatalogScript(script.Name, script.ContentHash, script.Statements.Count,
                    position + 1, beginning, database.Catalog.DdlGeneration, Schema?.Hash, script.CreatedObjects(position + 1)), cancellationToken).ConfigureAwait(false);
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
}
