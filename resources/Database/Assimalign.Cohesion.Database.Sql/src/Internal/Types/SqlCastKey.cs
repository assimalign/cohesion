using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A cast pair's storage ordinals and case-insensitive domain names, without formatted lookup strings.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCastKey")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCastKey.DebugView))]
internal readonly struct SqlCastKey : IEquatable<SqlCastKey>
{
    private readonly DatabaseType _source;
    private readonly DatabaseType _target;
    private readonly string? _sourceDomain;
    private readonly string? _targetDomain;

    internal SqlCastKey(DatabaseType source, string? sourceDomain, DatabaseType target, string? targetDomain)
    {
        // Domains are identified by name alone; built-ins ignore length, precision and scale.
        _source = sourceDomain is null ? source : DatabaseType.Null;
        _target = targetDomain is null ? target : DatabaseType.Null;
        _sourceDomain = sourceDomain;
        _targetDomain = targetDomain;
    }

    public bool Equals(SqlCastKey other) => _source == other._source && _target == other._target
        && string.Equals(_sourceDomain, other._sourceDomain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_targetDomain, other._targetDomain, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is SqlCastKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_source, _target,
        _sourceDomain is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_sourceDomain),
        _targetDomain is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_targetDomain));

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCastKey value)
        {
            _ = value;
        }
    }
}
