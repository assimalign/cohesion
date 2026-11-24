using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The users of a directory: the arguments of <see cref="IIdentityUserRegistry"/> checked, then
/// forwarded to the <see cref="IdentityObjectSet{TObject}"/> that serves every kind of
/// directory object.
/// </summary>
[DebuggerDisplay("{Locked.Count} locked")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class IdentityUserRegistry : IIdentityUserRegistry
{
    private readonly IdentityObjectSet<IdentityUser> _set;

    internal IdentityUserRegistry(IIdentityHubStore store, IEnumerable<IdentityUser>? defined, string parameterName)
    {
        _set = IdentityObjectSet<IdentityUser>.Create(store, IdentityUserCodec.Instance, defined, parameterName);
    }

    public IReadOnlyList<IdentityUser> Locked => _set.Locked;

    public ValueTask<IdentityUser?> FindAsync(IdentityUserId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.UserIdRequired, nameof(id));
        }

        return _set.FindAsync(id.Value, cancellationToken);
    }

    public ValueTask<IdentityUser?> FindByNameAsync(IdentityUserName name, CancellationToken cancellationToken = default)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.UserNameRequired, nameof(name));
        }

        return _set.FindByNameAsync(IdentityDirectoryGrammar.Canonicalize(name.Value), cancellationToken);
    }

    public ValueTask<IdentityObjectPage<IdentityUser>> ListAsync(
        int limit = IdentityHubRecordQuery.DefaultLimit,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        return _set.ListAsync(limit, continuation, cancellationToken);
    }

    public ValueTask<IdentityObjectWriteResult> TryAddAsync(IdentityUser value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        return _set.TryAddAsync(value, cancellationToken);
    }

    public ValueTask<IdentityObjectWriteResult> TryReplaceAsync(
        IdentityUser value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }

        return _set.TryReplaceAsync(value, expectedVersion, cancellationToken);
    }

    public ValueTask<IdentityObjectWriteResult> TryRemoveAsync(
        IdentityUserId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.UserIdRequired, nameof(id));
        }

        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }

        return _set.TryRemoveAsync(id.Value, expectedVersion, cancellationToken);
    }

    private sealed class DebugView
    {
        private readonly IdentityUserRegistry _registry;

        public DebugView(IdentityUserRegistry registry)
        {
            _registry = registry;
        }

        public IReadOnlyList<IdentityUser> Locked => _registry.Locked;
    }
}
