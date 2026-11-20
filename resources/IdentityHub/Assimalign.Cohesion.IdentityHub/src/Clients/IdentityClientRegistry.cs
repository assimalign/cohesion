using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The app registrations of an identity hub: a fixed set defined in code, served locked, over
/// the registrations kept in an <see cref="IIdentityHubStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registrations defined in code never reach the store.</b> They are held by the
/// registry, are the same on every node that runs the same code, and are gone when the code
/// that defines them is. Stored registrations live in the <c>clients</c> record set, which
/// keeps each audience unique across them.
/// </para>
/// <para>
/// <b>Code wins.</b> A stored registration with the identifier of one defined in code is not
/// served by <see cref="FindAsync"/>, <see cref="FindByAudienceAsync"/>, or
/// <see cref="ListAsync"/>, and a write may not claim an audience code already exposes. Such a
/// stored registration is left in the store: it is served again if the code that shadows it is
/// removed, and until then its audiences stay taken among stored registrations.
/// </para>
/// <para>
/// <b>That is also how code adopts a registration created at run time.</b> An identifier
/// derived with <see cref="IdentityClientId.FromName"/> is the same wherever the name is used,
/// so a registration stored under a name is shadowed by the first deployment whose code defines
/// that name. The registration from code is served whole: nothing of the stored one, a secret
/// issued to it included, carries over.
/// </para>
/// <para>
/// The registry holds no connection of its own, does not own the store, and is safe for
/// concurrent use when the store is.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IdentityClientRegistry registry = IdentityClientRegistry.Create(store,
/// [
///     IdentityClient.Create(new()
///     {
///         Id = IdentityClientId.FromName("orders-api"),
///         Audiences = { "api://orders" },
///         Scopes = { IdentityClientScope.Create("orders.read") },
///     }),
///     IdentityClient.Create(new()
///     {
///         Id = IdentityClientId.FromName("orders-web"),
///         Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.RefreshToken,
///         Redirects = { IdentityClientRedirect.Web("https://orders.example/signin") },
///         Permissions = { IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.read") },
///     }),
/// ]);
/// </code>
/// </example>
[DebuggerDisplay("{Locked.Count} locked")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class IdentityClientRegistry : IIdentityClientRegistry
{
    private const string lockedPrefix = "c";
    private const string storedPrefix = "s";

    private readonly IdentityHubRecordSet<IdentityClient> _stored;
    private readonly IdentityClient[] _locked;
    private readonly Dictionary<IdentityClientId, IdentityClient> _lockedById;
    private readonly Dictionary<IdentityClientAudience, IdentityClient> _lockedByAudience;

    private IdentityClientRegistry(
        IdentityHubRecordSet<IdentityClient> stored,
        IdentityClient[] locked,
        Dictionary<IdentityClientId, IdentityClient> lockedById,
        Dictionary<IdentityClientAudience, IdentityClient> lockedByAudience)
    {
        _stored = stored;
        _locked = locked;
        _lockedById = lockedById;
        _lockedByAudience = lockedByAudience;
        Locked = Array.AsReadOnly(locked);
    }

    /// <summary>
    /// Gets the registrations defined in code, in the order of their identifiers. Each is
    /// <see cref="IdentityClient.IsLocked"/>.
    /// </summary>
    public IReadOnlyList<IdentityClient> Locked { get; }

    /// <summary>
    /// Creates a registry over a store, with the registrations defined in code.
    /// </summary>
    /// <remarks>
    /// The registrations defined in code are checked against each other here, once, so a
    /// mistake in them stops the hub from starting instead of failing a request later: an
    /// identifier or an audience may appear once, and a permission one of them holds on another
    /// must name a scope, or a role open to applications, that the other exposes. A permission
    /// on a registration that is not defined in code is not checked: it may be stored.
    /// </remarks>
    /// <param name="store">The store that holds the registrations created at run time.</param>
    /// <param name="clients">The registrations defined in code, or <see langword="null"/> for none.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clients"/> holds a <see langword="null"/> registration, the same
    /// identifier or audience twice, or a permission another of them does not expose.
    /// </exception>
    public static IdentityClientRegistry Create(IIdentityHubStore store, IEnumerable<IdentityClient>? clients = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var byId = new Dictionary<IdentityClientId, IdentityClient>();
        var byAudience = new Dictionary<IdentityClientAudience, IdentityClient>();
        if (clients is not null)
        {
            foreach (IdentityClient client in clients)
            {
                if (client is null)
                {
                    throw new ArgumentException(ErrorMessages.RegistryClientRequired, nameof(clients));
                }

                IdentityClient locked = client.AsLocked();
                if (!byId.TryAdd(locked.Id, locked))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.RegistryClientDuplicate, IdentityClient.Describe(locked)),
                        nameof(clients));
                }

                for (int index = 0; index < locked.Audiences.Count; index++)
                {
                    IdentityClientAudience audience = locked.Audiences[index];
                    if (!byAudience.TryAdd(audience, locked))
                    {
                        throw new ArgumentException(
                            string.Format(
                                CultureInfo.CurrentCulture,
                                ErrorMessages.RegistryAudienceDuplicate,
                                audience,
                                IdentityClient.Describe(byAudience[audience]),
                                IdentityClient.Describe(locked)),
                            nameof(clients));
                    }
                }
            }
        }

        var ordered = new IdentityClient[byId.Count];
        byId.Values.CopyTo(ordered, 0);
        // Guid's order is the ordinal order of the identifiers' text, which is the order a store
        // keeps the stored registrations' keys in.
        Array.Sort(ordered, static (left, right) => left.Id.Value.CompareTo(right.Id.Value));

        for (int index = 0; index < ordered.Length; index++)
        {
            IdentityClient holder = ordered[index];
            for (int position = 0; position < holder.Permissions.Count; position++)
            {
                IdentityClientPermission permission = holder.Permissions[position];
                if (byId.TryGetValue(permission.Resource, out IdentityClient? resource) && !resource.Exposes(permission))
                {
                    throw new ArgumentException(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            permission.Kind is IdentityClientPermissionKind.Delegated
                                ? ErrorMessages.ClientScopeUnknown
                                : ErrorMessages.ClientRoleUnknown,
                            IdentityClient.Describe(holder),
                            permission.Name,
                            IdentityClient.Describe(resource)),
                        nameof(clients));
                }
            }
        }

        return new IdentityClientRegistry(
            IdentityHubRecordSet<IdentityClient>.Create(store, IdentityClientCodec.Instance),
            ordered,
            byId,
            byAudience);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClient?> FindAsync(IdentityClientId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.ClientIdRequired, nameof(id));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IdentityClient?>(cancellationToken);
        }

        return _lockedById.TryGetValue(id, out IdentityClient? locked)
            ? new ValueTask<IdentityClient?>(locked)
            : FindStoredAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClient?> FindByAudienceAsync(IdentityClientAudience audience, CancellationToken cancellationToken = default)
    {
        if (audience.Value is null)
        {
            throw new ArgumentException(ErrorMessages.AudienceRequired, nameof(audience));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IdentityClient?>(cancellationToken);
        }

        return _lockedByAudience.TryGetValue(audience, out IdentityClient? locked)
            ? new ValueTask<IdentityClient?>(locked)
            : FindStoredByAudienceAsync(audience, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClientPage> ListAsync(
        int limit = IdentityClientPage.DefaultLimit,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IdentityClientPage.MaxLimit);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IdentityClientPage>(cancellationToken);
        }

        // A continuation is "c<offset>" while registrations defined in code remain, and
        // "s<store token>" once the stored ones are being read ("s" alone starts them).
        int offset = 0;
        string? storeToken = null;
        bool readsStore = false;
        if (continuation is not null)
        {
            if (continuation.StartsWith(storedPrefix, StringComparison.Ordinal))
            {
                readsStore = true;
                storeToken = continuation.Length is 1 ? null : continuation[1..];
            }
            else if (!continuation.StartsWith(lockedPrefix, StringComparison.Ordinal) ||
                !int.TryParse(continuation.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                offset < 1 ||
                offset >= _locked.Length)
            {
                return ValueTask.FromException<IdentityClientPage>(
                    IdentityHubStoreException.ContinuationInvalid(_stored.Name));
            }
        }

        var clients = new List<IdentityClient>();
        if (!readsStore)
        {
            int count = Math.Min(limit, _locked.Length - offset);
            for (int index = 0; index < count; index++)
            {
                clients.Add(_locked[offset + index]);
            }

            offset += count;
            if (offset < _locked.Length)
            {
                return new ValueTask<IdentityClientPage>(new IdentityClientPage(
                    clients,
                    string.Concat(lockedPrefix, offset.ToString(CultureInfo.InvariantCulture))));
            }

            if (clients.Count == limit)
            {
                return new ValueTask<IdentityClientPage>(new IdentityClientPage(clients, storedPrefix));
            }
        }

        return ListStoredAsync(clients, limit - clients.Count, storeToken, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClientWriteResult> TryAddAsync(IdentityClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        return TryRefuse(client, out IdentityClientWriteResult refusal)
            ? new ValueTask<IdentityClientWriteResult>(refusal)
            : AddStoredAsync(client, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClientWriteResult> TryReplaceAsync(
        IdentityClient client,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }

        return TryRefuse(client, out IdentityClientWriteResult refusal)
            ? new ValueTask<IdentityClientWriteResult>(refusal)
            : ReplaceStoredAsync(client, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityClientWriteResult> TryRemoveAsync(
        IdentityClientId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.ClientIdRequired, nameof(id));
        }

        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }

        return _lockedById.ContainsKey(id)
            ? new ValueTask<IdentityClientWriteResult>(IdentityClientWriteResult.Locked)
            : RemoveStoredAsync(id, expectedVersion, cancellationToken);
    }

    private bool TryRefuse(IdentityClient value, out IdentityClientWriteResult refusal)
    {
        if (_lockedById.ContainsKey(value.Id))
        {
            refusal = IdentityClientWriteResult.Locked;
            return true;
        }

        for (int index = 0; index < value.Audiences.Count; index++)
        {
            if (_lockedByAudience.ContainsKey(value.Audiences[index]))
            {
                refusal = IdentityClientWriteResult.AudienceConflict(value.Audiences[index]);
                return true;
            }
        }

        refusal = default;
        return false;
    }

    private async ValueTask<IdentityClient?> FindStoredAsync(IdentityClientId id, CancellationToken cancellationToken)
    {
        IdentityHubEntry<IdentityClient>? entry =
            await _stored.FindAsync(IdentityClientCodec.KeyOf(id), cancellationToken).ConfigureAwait(false);
        return entry?.Value.AsStored(entry.Version);
    }

    private async ValueTask<IdentityClient?> FindStoredByAudienceAsync(
        IdentityClientAudience audience,
        CancellationToken cancellationToken)
    {
        IdentityHubEntry<IdentityClient>? entry = await _stored
            .FindByIndexAsync(IdentityClientCodec.AudienceIndex, new IdentityHubRecordKey(audience.Value), cancellationToken)
            .ConfigureAwait(false);

        // A stored registration that code shadows is not served under any of its names.
        return entry is null || _lockedById.ContainsKey(entry.Value.Id)
            ? null
            : entry.Value.AsStored(entry.Version);
    }

    private async ValueTask<IdentityClientPage> ListStoredAsync(
        List<IdentityClient> clients,
        int limit,
        string? storeToken,
        CancellationToken cancellationToken)
    {
        IdentityHubEntryPage<IdentityClient> page = await _stored
            .QueryAsync(IdentityHubRecordQuery.All(limit, storeToken), cancellationToken)
            .ConfigureAwait(false);

        for (int index = 0; index < page.Entries.Count; index++)
        {
            IdentityHubEntry<IdentityClient> entry = page.Entries[index];
            if (!_lockedById.ContainsKey(entry.Value.Id))
            {
                clients.Add(entry.Value.AsStored(entry.Version));
            }
        }

        return new IdentityClientPage(
            clients,
            page.Continuation is null ? null : string.Concat(storedPrefix, page.Continuation));
    }

    private async ValueTask<IdentityClientWriteResult> AddStoredAsync(IdentityClient value, CancellationToken cancellationToken)
    {
        IdentityHubWriteResult result = await _stored.TryAddAsync(value, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            IdentityHubWriteStatus.Written => IdentityClientWriteResult.Written(result.Version),
            IdentityHubWriteStatus.KeyConflict => IdentityClientWriteResult.IdConflict,
            IdentityHubWriteStatus.IndexConflict => IdentityClientWriteResult.AudienceConflict(
                await FindTakenAudienceAsync(value, cancellationToken).ConfigureAwait(false)),
            _ => throw Unexpected(result.Status),
        };
    }

    private async ValueTask<IdentityClientWriteResult> ReplaceStoredAsync(
        IdentityClient value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        IdentityHubWriteResult result =
            await _stored.TryReplaceAsync(value, expectedVersion, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            IdentityHubWriteStatus.Written => IdentityClientWriteResult.Written(result.Version),
            IdentityHubWriteStatus.VersionConflict => IdentityClientWriteResult.VersionConflict,
            IdentityHubWriteStatus.IndexConflict => IdentityClientWriteResult.AudienceConflict(
                await FindTakenAudienceAsync(value, cancellationToken).ConfigureAwait(false)),
            _ => throw Unexpected(result.Status),
        };
    }

    private async ValueTask<IdentityClientWriteResult> RemoveStoredAsync(
        IdentityClientId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        bool removed = await _stored
            .TryRemoveAsync(IdentityClientCodec.KeyOf(id), expectedVersion, cancellationToken)
            .ConfigureAwait(false);
        return removed ? IdentityClientWriteResult.Removed : IdentityClientWriteResult.VersionConflict;
    }

    /// <summary>
    /// Names the audience a refused write lost to another stored registration. The store reports
    /// only that a unique index entry was taken, so each audience is looked up; the answer is
    /// <see langword="default"/> when the other registration is gone again by then.
    /// </summary>
    private async ValueTask<IdentityClientAudience> FindTakenAudienceAsync(IdentityClient value, CancellationToken cancellationToken)
    {
        for (int index = 0; index < value.Audiences.Count; index++)
        {
            IdentityClientAudience audience = value.Audiences[index];
            IdentityHubEntry<IdentityClient>? holder = await _stored
                .FindByIndexAsync(IdentityClientCodec.AudienceIndex, new IdentityHubRecordKey(audience.Value), cancellationToken)
                .ConfigureAwait(false);
            if (holder is not null && holder.Value.Id != value.Id)
            {
                return audience;
            }
        }

        return default;
    }

    private IdentityHubStoreException Unexpected(IdentityHubWriteStatus status)
    {
        return new IdentityHubStoreException(
            IdentityHubErrorCode.Unknown,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.StoreStatusUnexpected, _stored.Name, status))
        {
            Set = _stored.Name,
        };
    }

    private sealed class DebugView
    {
        private readonly IdentityClientRegistry _registry;

        public DebugView(IdentityClientRegistry registry)
        {
            _registry = registry;
        }

        public IReadOnlyList<IdentityClient> Locked => _registry.Locked;
    }
}
