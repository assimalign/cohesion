using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private readonly Dictionary<string, DefinitionSlot<SqlCatalogScript>> _scripts = new(StringComparer.Ordinal);
    private SqlCatalogScriptState? _scriptState;
    private (PageId PageId, int SlotIndex)? _scriptStateLocation;

    /// <summary>Gets the immutable durable script receipts, including interrupted prefixes.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<SqlCatalogScript> Scripts
    {
        get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_scripts.Values.Select(slot => slot.Value).ToArray()); } }
    }
    /// <summary>Gets the constant-time script-set reconciliation marker.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public SqlCatalogScriptState? ScriptState
    {
        get { lock (_sync) { ThrowIfOffline(); return _scriptState; } }
    }

    /// <summary>Records a script's identity before any statements execute, or returns its matching durable receipt.</summary>
    /// <param name="name">The stable, case-sensitive script name.</param>
    /// <param name="contentHash">The SHA-256 of the exact UTF-8 script body.</param>
    /// <param name="statementCount">The parsed statement count.</param>
    /// <param name="schemaHash">The typed schema hash at execution, or null.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    /// <returns>The matching durable receipt, initially with no completed statements or object claims.</returns>
    /// <exception cref="ArgumentException">A name, digest or statement count is invalid.</exception>
    /// <exception cref="SqlCatalogException">The script identity changed or the record is too large.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public ValueTask<SqlCatalogScript> BeginScriptAsync(
        string name,
        string contentHash,
        int statementCount,
        string? schemaHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            var receipt = new SqlCatalogScript(name, contentHash, statementCount, 0, _ddlGeneration, _ddlGeneration, schemaHash);
            if (_scripts.TryGetValue(name, out var existing))
            {
                if (existing.Value.ContentHash != contentHash || existing.Value.StatementCount != statementCount || existing.Value.SchemaHash != schemaHash)
                {
                    throw new SqlCatalogException("COHSQLS001: A declared script checksum, statement count or typed schema identity changed.");
                }
                return ValueTask.FromResult(existing.Value);
            }
            using var transaction = _storage.BeginTransaction();
            var slot = StageScript(transaction, receipt);
            transaction.Commit();
            _scripts[receipt.Name] = slot;
            return ValueTask.FromResult(receipt);
        }
    }

    /// <summary>Completes one non-DDL statement after its read or independently committed data transaction succeeds.</summary>
    /// <param name="publication">The expected preceding receipt and catalog generation, with an empty expected object.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    /// <returns>A completed durable operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The preceding state changed, the publication targets a DDL object, or the receipt exceeds the record limit.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public ValueTask CompleteScriptStatementAsync(
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            ValidateScriptPublication(publication, string.Empty);
            using var transaction = _storage.BeginTransaction();
            var slot = StageScript(transaction, CreateScriptReceipt(publication, _ddlGeneration, string.Empty, false, false));
            transaction.Commit();
            _scripts[publication.Name] = slot;
        }
        return default;
    }

    /// <summary>Saves the script-set marker after all declared scripts were reconciled.</summary>
    /// <param name="state">The compiled declaration digest and current generation.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    /// <returns>A completed durable operation.</returns>
    /// <exception cref="SqlCatalogException">The generation changed or the digest is invalid.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public ValueTask SaveScriptStateAsync(
        SqlCatalogScriptState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (state.Generation != _ddlGeneration || state.ContentHash.Length != 64 || !state.ContentHash.All(Uri.IsHexDigit))
            {
                throw new SqlCatalogException("Invalid script-set reconciliation marker.");
            }
            using var transaction = _storage.BeginTransaction();
            var record = new DatabaseKeyWriter().AppendInt32(13).AppendString(state.ContentHash, Collation.Binary).AppendInt64((long)state.Generation).ToArray();
            var location = UpsertRecord(transaction, _scriptStateLocation, record);
            transaction.Commit();
            _scriptStateLocation = location;
            _scriptState = state;
        }
        return default;
    }

    private DefinitionSlot<SqlCatalogScript> StageScript(StorageTransaction transaction, SqlCatalogScript receipt)
    {
        _scripts.TryGetValue(receipt.Name, out var existing);
        if (existing is not null && (existing.Value.ContentHash != receipt.ContentHash ||
            existing.Value.StatementCount != receipt.StatementCount || existing.Value.BaseGeneration != receipt.BaseGeneration ||
            existing.Value.CompletedStatements > receipt.CompletedStatements))
        {
            throw new SqlCatalogException("COHSQLS001: A declared script checksum, statement count or original generation changed, or its completed prefix regressed.");
        }
        var record = new DatabaseKeyWriter().AppendInt32(12)
            .AppendString(JsonSerializer.Serialize(receipt, SqlCatalogJsonContext.Default.SqlCatalogScript), Collation.Binary).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new SqlCatalogException("COHSQLS001: Declared script receipt exceeds the catalog record limit.");
        }
        return new DefinitionSlot<SqlCatalogScript>(receipt, UpsertRecord(transaction, existing?.Location, record));
    }

    private void ValidateScriptPublication(SqlCatalogScriptPublication publication, string actualObject)
    {
        _scripts.TryGetValue(publication.Name, out var existing);
        var previous = existing?.Value;
        if (publication.ExpectedGeneration != _ddlGeneration ||
            !string.Equals(publication.ExpectedObject, actualObject, StringComparison.OrdinalIgnoreCase) ||
            !SameReceipt(previous, publication.Previous) || publication.CompletedStatements > publication.StatementCount ||
            previous is not null && (previous.Name != publication.Name || previous.ContentHash != publication.ContentHash ||
                previous.StatementCount != publication.StatementCount || previous.SchemaHash != publication.SchemaHash))
        {
            throw new SqlCatalogException("COHSQLS001: The statement target, script identity or expected preceding receipt state does not match this publication.");
        }
    }

    private static bool SameReceipt(SqlCatalogScript? actual, SqlCatalogScript? expected)
        => actual is null ? expected is null : expected is not null &&
            actual.Name == expected.Name && actual.ContentHash == expected.ContentHash &&
            actual.StatementCount == expected.StatementCount && actual.CompletedStatements == expected.CompletedStatements &&
            actual.BaseGeneration == expected.BaseGeneration && actual.Generation == expected.Generation &&
            actual.SchemaHash == expected.SchemaHash && actual.Objects.SequenceEqual(expected.Objects, StringComparer.Ordinal) &&
            actual.StatementObjects.SequenceEqual(expected.StatementObjects, StringComparer.Ordinal) &&
            actual.StatementParameterTypes.Count == expected.StatementParameterTypes.Count &&
            actual.StatementParameterTypes.Select((types, position) =>
                types.SequenceEqual(expected.StatementParameterTypes[position], StringComparer.Ordinal)).All(equal => equal);

    private void CommitScriptAbsence(SqlCatalogScriptPublication publication, string actualObject)
    {
        ValidateScriptPublication(publication, actualObject);
        using var transaction = _storage.BeginTransaction();
        var script = StageScript(transaction, CreateScriptReceipt(publication, _ddlGeneration, actualObject, false, false));
        transaction.Commit();
        _scripts[publication.Name] = script;
    }

    private static SqlCatalogScript CreateScriptReceipt(
        SqlCatalogScriptPublication publication,
        ulong generation,
        string actualObject,
        bool createsObject,
        bool reservationOnly,
        IReadOnlyList<string>? parameterTypes = null)
    {
        IReadOnlyList<string> objects = publication.Previous?.Objects ?? [];
        IReadOnlyList<string> statementObjects = publication.Previous?.StatementObjects ?? [];
        IReadOnlyList<IReadOnlyList<string>> statementParameterTypes = publication.Previous?.StatementParameterTypes ?? [];
        if (!reservationOnly)
        {
            statementObjects = statementObjects.Append(actualObject).ToArray();
            statementParameterTypes = statementParameterTypes.Append(parameterTypes ?? []).ToArray();
            if (createsObject)
            {
                objects = objects.Append(actualObject).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        return new SqlCatalogScript(publication.Name, publication.ContentHash, publication.StatementCount,
            publication.CompletedStatements - (reservationOnly ? 1 : 0), publication.BaseGeneration,
            generation, publication.SchemaHash, objects, statementObjects, statementParameterTypes);
    }

}
