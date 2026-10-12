using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private const int functionRecordKind = 10;
    private const int domainRecordKind = 11;
    private readonly Dictionary<string, DefinitionSlot<SqlCatalogFunction>> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DefinitionSlot<SqlCatalogDomain>> _domains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets an immutable capture of the SQL function definitions.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<SqlCatalogFunction> Functions
    {
        get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_functions.Values.Select(slot => slot.Value).ToArray()); } }
    }

    /// <summary>Gets an immutable capture of the SQL domain definitions.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<SqlCatalogDomain> Domains
    {
        get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_domains.Values.Select(slot => slot.Value).ToArray()); } }
    }

    /// <summary>Publishes a new SQL function and advances the DDL generation atomically.</summary>
    /// <param name="function">The validated canonical definition.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <param name="scriptReceipt">The optional statement receipt committed atomically with this publication; never applied to another call.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The signature exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="SqlCatalogException">The supplied script receipt is not the next incomplete statement, has changed its checksum, statement count or original generation, or exceeds the record limit.</exception>
    public ValueTask CreateFunctionAsync(SqlCatalogFunction function, CancellationToken cancellationToken = default, SqlCatalogScript? scriptReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        return CreateDefinitionAsync(_functions, function.Signature, function, functionRecordKind,
            SqlCatalogJsonContext.Default.SqlCatalogFunction, cancellationToken, scriptReceipt);
    }

    /// <summary>Publishes a new SQL domain and advances the DDL generation atomically.</summary>
    /// <param name="domain">The validated canonical definition.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <param name="scriptReceipt">The optional statement receipt committed atomically with this publication; never applied to another call.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The name exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="SqlCatalogException">The supplied script receipt is not the next incomplete statement, has changed its checksum, statement count or original generation, or exceeds the record limit.</exception>
    public ValueTask CreateDomainAsync(SqlCatalogDomain domain, CancellationToken cancellationToken = default, SqlCatalogScript? scriptReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return CreateDefinitionAsync(_domains, domain.Name, domain, domainRecordKind,
            SqlCatalogJsonContext.Default.SqlCatalogDomain, cancellationToken, scriptReceipt);
    }

    /// <summary>Drops a SQL function after the engine has checked ownership and dependencies.</summary>
    /// <param name="signature">The normalized overload identity.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <param name="scriptReceipt">The optional statement receipt committed atomically with this publication; never applied to another call.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The signature does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="SqlCatalogException">The supplied script receipt is not the next incomplete statement, has changed its checksum, statement count or original generation, or exceeds the record limit.</exception>
    public ValueTask DropFunctionAsync(string signature, CancellationToken cancellationToken = default, SqlCatalogScript? scriptReceipt = null)
        => DropDefinitionAsync(_functions, signature, cancellationToken, scriptReceipt);

    /// <summary>Drops a SQL domain after the engine has checked ownership and dependencies.</summary>
    /// <param name="name">The domain name.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <param name="scriptReceipt">The optional statement receipt committed atomically with this publication; never applied to another call.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The name does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="SqlCatalogException">The supplied script receipt is not the next incomplete statement, has changed its checksum, statement count or original generation, or exceeds the record limit.</exception>
    public ValueTask DropDomainAsync(string name, CancellationToken cancellationToken = default, SqlCatalogScript? scriptReceipt = null)
        => DropDefinitionAsync(_domains, name, cancellationToken, scriptReceipt);

    private ValueTask CreateDefinitionAsync<T>(Dictionary<string, DefinitionSlot<T>> directory, string key, T value,
        int kind, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, SqlCatalogScript? scriptReceipt)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] record = new DatabaseKeyWriter().AppendInt32(kind).AppendString(JsonSerializer.Serialize(value, typeInfo), Collation.Binary).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new SqlCatalogException($"SQL definition '{key}' exceeds the catalog record limit of {SlottedPage.MaxRecordSize} bytes.");
        }

        lock (_sync)
        {
            ThrowIfOffline();
            if (directory.ContainsKey(key))
            {
                throw new SqlCatalogException($"SQL definition '{key}' already exists.");
            }
            using var transaction = _storage.BeginTransaction();
            var location = _storage.InsertRow(transaction, record);
            CommitCatalogChange(transaction, scriptReceipt: scriptReceipt);
            directory.Add(key, new DefinitionSlot<T>(value, location));
        }
        return default;
    }

    private ValueTask DropDefinitionAsync<T>(Dictionary<string, DefinitionSlot<T>> directory, string key, CancellationToken cancellationToken, SqlCatalogScript? scriptReceipt)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (!directory.TryGetValue(key, out var slot))
            {
                throw new SqlCatalogException($"SQL definition '{key}' does not exist.");
            }
            using var transaction = _storage.BeginTransaction();
            _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
            CommitCatalogChange(transaction, scriptReceipt: scriptReceipt);
            directory.Remove(key);
        }
        return default;
    }

    private void LoadDefinition(int kind, string json, (PageId PageId, int SlotIndex) location)
    {
        try
        {
            if (kind == functionRecordKind)
            {
                var value = JsonSerializer.Deserialize(json, SqlCatalogJsonContext.Default.SqlCatalogFunction)
                    ?? throw new SqlCatalogException("Missing SQL function definition.");
                if (!_functions.TryAdd(value.Signature, new DefinitionSlot<SqlCatalogFunction>(value, location)))
                {
                    throw new SqlCatalogException($"Duplicate SQL function '{value.Signature}'.");
                }

            }
            else
            {
                var value = JsonSerializer.Deserialize(json, SqlCatalogJsonContext.Default.SqlCatalogDomain)
                    ?? throw new SqlCatalogException("Missing SQL domain definition.");
                if (!_domains.TryAdd(value.Name, new DefinitionSlot<SqlCatalogDomain>(value, location)))
                {
                    throw new SqlCatalogException($"Duplicate SQL domain '{value.Name}'.");
                }

            }
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new SqlCatalogException($"Malformed SQL definition: {failure.Message}");
        }
    }

    private sealed record DefinitionSlot<T>(T Value, (PageId PageId, int SlotIndex) Location);
}
