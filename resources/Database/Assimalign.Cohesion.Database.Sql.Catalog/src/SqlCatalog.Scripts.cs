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
    private readonly AsyncLocal<SqlCatalogScript?> _scriptStatement = new();
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

    /// <summary>Enlists a statement receipt in catalog mutations performed within the returned scope.</summary>
    /// <remarks>Object-id reservations advance the generation without completing the statement. Final publication and its receipt commit atomically.</remarks>
    /// <param name="receipt">The prefix including the statement being executed.</param>
    /// <returns>A scope restoring the previous receipt.</returns>
    /// <exception cref="ArgumentNullException">The receipt is null.</exception>
    public IDisposable BeginScriptStatement(SqlCatalogScript receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var previous = _scriptStatement.Value;
        _scriptStatement.Value = receipt;
        return new ScriptScope(_scriptStatement, previous);
    }

    /// <summary>Saves a receipt after a read or an independently committed data transaction.</summary>
    /// <param name="receipt">The durable prefix.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    /// <returns>A completed durable operation.</returns>
    /// <exception cref="SqlCatalogException">The checksum changed, progress regressed, or the record is too large.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public ValueTask SaveScriptAsync(SqlCatalogScript receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            using var transaction = _storage.BeginTransaction();
            var slot = StageScript(transaction, receipt);
            transaction.Commit();
            _scripts[receipt.Name] = slot;
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
    public ValueTask SaveScriptStateAsync(SqlCatalogScriptState state, CancellationToken cancellationToken = default)
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
        if (existing is not null && (existing.Value.ContentHash != receipt.ContentHash || existing.Value.CompletedStatements > receipt.CompletedStatements))
        {
            throw new SqlCatalogException("COHSQLS001: A declared script checksum changed or its completed prefix regressed.");
        }
        var record = new DatabaseKeyWriter().AppendInt32(12)
            .AppendString(JsonSerializer.Serialize(receipt, SqlCatalogJsonContext.Default.SqlCatalogScript), Collation.Binary).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new SqlCatalogException("Declared script receipt exceeds the catalog record limit.");
        }
        return new DefinitionSlot<SqlCatalogScript>(receipt, UpsertRecord(transaction, existing?.Location, record));
    }

    private sealed class ScriptScope : IDisposable
    {
        private readonly AsyncLocal<SqlCatalogScript?> _slot;
        private readonly SqlCatalogScript? _previous;
        internal ScriptScope(AsyncLocal<SqlCatalogScript?> slot, SqlCatalogScript? previous) { _slot = slot; _previous = previous; }
        public void Dispose() => _slot.Value = _previous;
    }
}
