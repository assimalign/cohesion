using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The groups of a directory: the arguments of <see cref="IIdentityGroupRegistry"/> checked,
/// then forwarded to the <see cref="IdentityObjectSet{TObject}"/> that serves every kind of
/// directory object.
/// </summary>
[DebuggerDisplay("{Locked.Count} locked")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class IdentityGroupRegistry : IIdentityGroupRegistry
{
    private readonly IdentityObjectSet<IdentityGroup> _set;

    internal IdentityGroupRegistry(IIdentityHubStore store, IEnumerable<IdentityGroup>? defined, string parameterName)
    {
        _set = IdentityObjectSet<IdentityGroup>.Create(store, IdentityGroupCodec.Instance, defined, parameterName);
    }

    public IReadOnlyList<IdentityGroup> Locked => _set.Locked;

    public ValueTask<IdentityGroup?> FindAsync(IdentityGroupId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.GroupIdRequired, nameof(id));
        }

        return _set.FindAsync(id.Value, cancellationToken);
    }

    public ValueTask<IdentityGroup?> FindByNameAsync(IdentityGroupName name, CancellationToken cancellationToken = default)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GroupNameRequired, nameof(name));
        }

        return _set.FindByNameAsync(IdentityDirectoryGrammar.Canonicalize(name.Value), cancellationToken);
    }

    public ValueTask<IdentityObjectPage<IdentityGroup>> ListAsync(
        int limit = IdentityHubRecordQuery.DefaultLimit,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        return _set.ListAsync(limit, continuation, cancellationToken);
    }

    public ValueTask<IdentityObjectWriteResult> TryAddAsync(IdentityGroup value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        return _set.TryAddAsync(value, cancellationToken);
    }

    public ValueTask<IdentityObjectWriteResult> TryReplaceAsync(
        IdentityGroup value,
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
        IdentityGroupId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.GroupIdRequired, nameof(id));
        }

        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }

        return _set.TryRemoveAsync(id.Value, expectedVersion, cancellationToken);
    }

    private sealed class DebugView
    {
        private readonly IdentityGroupRegistry _registry;

        public DebugView(IdentityGroupRegistry registry)
        {
            _registry = registry;
        }

        public IReadOnlyList<IdentityGroup> Locked => _registry.Locked;
    }
}
