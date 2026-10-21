using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
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
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The signature exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public ValueTask CreateFunctionAsync(
        SqlCatalogFunction function,
        CancellationToken cancellationToken = default)
        => CreateFunctionCoreAsync(function, cancellationToken, publication: null);

    /// <summary>Publishes a new SQL function and advances the DDL generation atomically.</summary>
    /// <param name="function">The validated canonical definition.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The signature exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask CreateFunctionAsync(
        SqlCatalogFunction function,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return CreateFunctionCoreAsync(function, cancellationToken, publication);
    }

    private ValueTask CreateFunctionCoreAsync(
        SqlCatalogFunction function,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(function);
        return CreateDefinitionAsync(_functions, function.Signature, function, functionRecordKind,
            SqlCatalogJsonContext.Default.SqlCatalogFunction, cancellationToken, publication, "F:" + function.Signature);
    }

    /// <summary>Publishes a new SQL domain and advances the DDL generation atomically.</summary>
    /// <param name="domain">The validated canonical definition.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The name exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public ValueTask CreateDomainAsync(
        SqlCatalogDomain domain,
        CancellationToken cancellationToken = default)
        => CreateDomainCoreAsync(domain, cancellationToken, publication: null);

    /// <summary>Publishes a new SQL domain and advances the DDL generation atomically.</summary>
    /// <param name="domain">The validated canonical definition.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="SqlCatalogException">The name exists or the record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask CreateDomainAsync(
        SqlCatalogDomain domain,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return CreateDomainCoreAsync(domain, cancellationToken, publication);
    }

    private ValueTask CreateDomainCoreAsync(
        SqlCatalogDomain domain,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return CreateDefinitionAsync(_domains, domain.Name, domain, domainRecordKind,
            SqlCatalogJsonContext.Default.SqlCatalogDomain, cancellationToken, publication, SqlObjectIdentity.Domain(domain.Name));
    }

    /// <summary>Drops a SQL function after the engine has checked ownership and dependencies.</summary>
    /// <param name="signature">The normalized overload identity.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The signature does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public ValueTask DropFunctionAsync(
        string signature,
        CancellationToken cancellationToken = default)
        => DropFunctionCoreAsync(signature, cancellationToken, publication: null, requestedParameterTypes: null);

    /// <summary>Drops a SQL function after the engine has checked ownership and dependencies.</summary>
    /// <param name="signature">The normalized overload identity.</param>
    /// <param name="parameterTypes">The ordered requested SQL type spellings already resolved by the caller to <paramref name="signature"/>, retaining equivalent domain aliases for receipt validation after reopen.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The signature does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="parameterTypes"/> is null.</exception>
    /// <exception cref="ArgumentException">A requested parameter type is blank.</exception>
    /// <exception cref="SqlCatalogException">The target, parameter count or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    /// <remarks>The SQL caller resolves the requested types, including native domains, to the normalized signature. The catalog validates the signature against the removed definition and records the requested spellings; it does not own the engine's native type registry.</remarks>
    public ValueTask DropFunctionAsync(
        string signature,
        IReadOnlyList<string> parameterTypes,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        var requestedParameterTypes = Array.AsReadOnly(parameterTypes.ToArray());
        if (requestedParameterTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A requested function parameter type cannot be blank.", nameof(parameterTypes));
        }
        return DropFunctionCoreAsync(signature, cancellationToken, publication, requestedParameterTypes);
    }

    private ValueTask DropFunctionCoreAsync(
        string signature,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication,
        IReadOnlyList<string>? requestedParameterTypes)
        => DropDefinitionAsync(_functions, signature, cancellationToken, publication, "F:" + signature, requestedParameterTypes);

    /// <summary>Drops a SQL domain after the engine has checked ownership and dependencies.</summary>
    /// <param name="name">The domain name.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The name does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public ValueTask DropDomainAsync(
        string name,
        CancellationToken cancellationToken = default)
        => DropDomainCoreAsync(name, cancellationToken, publication: null);

    /// <summary>Drops a SQL domain after the engine has checked ownership and dependencies.</summary>
    /// <param name="name">The domain name.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="SqlCatalogException">The name does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask DropDomainAsync(
        string name,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropDomainCoreAsync(name, cancellationToken, publication);
    }

    private ValueTask DropDomainCoreAsync(
        string name,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
        => DropDefinitionAsync(_domains, name, cancellationToken, publication, SqlObjectIdentity.Domain(name));

    private ValueTask CreateDefinitionAsync<T>(Dictionary<string, DefinitionSlot<T>> directory, string key, T value,
        int kind, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, SqlCatalogScriptPublication? publication, string actualObject)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] record = new DatabaseKeyWriter().AppendInt32(kind).AppendString(JsonSerializer.Serialize(value, typeInfo), Collation.Binary).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new SqlCatalogException("COHSQLE057", $"SQL definition '{key}' exceeds the catalog record limit of {SlottedPage.MaxRecordSize} bytes.");
        }

        lock (_sync)
        {
            ThrowIfOffline();
            if (directory.ContainsKey(key))
            {
                throw new SqlCatalogException("COHSQLE050", $"SQL definition '{key}' already exists.");
            }
            using var transaction = _storage.BeginTransaction();
            var location = _storage.InsertRow(transaction, record);
            CommitCatalogChange(transaction, publication: publication, actualObject: actualObject,
                createsObject: value is SqlCatalogNamespace,
                parameterTypes: value is SqlCatalogFunction function ? function.ParameterTypes : null);
            directory.Add(key, new DefinitionSlot<T>(value, location));
            if (value is SqlCatalogNamespace) { _namespaceCapture = null; }
        }
        return default;
    }

    private ValueTask DropDefinitionAsync<T>(Dictionary<string, DefinitionSlot<T>> directory, string key, CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication, string actualObject, IReadOnlyList<string>? requestedParameterTypes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (!directory.TryGetValue(key, out var slot))
            {
                throw new SqlCatalogException("COHSQLE052", $"SQL definition '{key}' does not exist.");
            }
            if (slot.Value is SqlCatalogFunction function)
            {
                actualObject = "F:" + function.Signature;
                if (requestedParameterTypes is not null && requestedParameterTypes.Count != function.ParameterTypes.Count)
                {
                    throw new SqlCatalogException("COHSQLS001: The requested parameter count does not match the function being removed.");
                }
                requestedParameterTypes ??= function.ParameterTypes;
            }
            var dependentView = _views.Values.Select(value => value.Value).FirstOrDefault(view => view.Dependencies.Contains(actualObject, StringComparer.OrdinalIgnoreCase) || view.FunctionCalls.Contains(actualObject, StringComparer.OrdinalIgnoreCase));
            if (dependentView is not null) { RefuseViewDependency(dependentView); }
            using var transaction = _storage.BeginTransaction();
            _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
            CommitCatalogChange(transaction, publication: publication, actualObject: actualObject,
                parameterTypes: requestedParameterTypes);
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
