using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The types and casts a built engine knows, frozen when its build froze the builder's
/// <see cref="SqlTypeCollection"/>: <see cref="SqlDatabaseEngine.Types"/>.
/// </summary>
/// <remarks>
/// Immutable, so every session reads it without a lock. DDL and <c>CAST</c> resolve a name that is
/// not a built-in type name here, once per statement, never per row; a <c>CAST</c> binds the casts it
/// may apply when its statement is planned. There is no run-time registration.
/// </remarks>
public sealed class SqlTypeCatalog : IReadOnlyCollection<SqlType>
{
    // One slot per storage type, indexed by its value: the casts a bound CAST may apply.
    private const int storageTypeCount = (int)DatabaseType.JsonBinary + 1;

    private readonly SqlType[] _types;
    private readonly SqlCast[] _casts;
    private readonly FrozenDictionary<string, SqlType> _domains;
    private readonly FrozenDictionary<string, SqlCast> _castsByKey;

    /// <summary>Freezes registrations into a catalog.</summary>
    /// <param name="types">The types, already validated, in registration order.</param>
    /// <param name="casts">The casts, already validated, in registration order.</param>
    internal SqlTypeCatalog(IEnumerable<SqlType> types, IEnumerable<SqlCast> casts)
    {
        _types = types.ToArray();
        _casts = casts.ToArray();
        _domains = _types.Where(static type => type.IsDomain)
            .ToFrozenDictionary(static type => type.Name, StringComparer.OrdinalIgnoreCase);
        _castsByKey = _casts.ToFrozenDictionary(static cast => CastKey(cast.Source, cast.Target), StringComparer.Ordinal);
        HasDomainChecks = _types.Any(static type => type.Check is not null);
        HasApplicationCasts = _casts.Any(static cast => !SqlStandardCasts.Contains(cast));
    }

    /// <summary>Gets the built-in types and casts alone: the catalog of an engine created by <see cref="SqlDatabaseEngine.Create"/>.</summary>
    internal static SqlTypeCatalog Standard { get; } = new SqlTypeCollection(null).Freeze();

    /// <summary>
    /// Gets whether a domain has a check: an application's code a write or a <c>CAST</c> runs, whose
    /// context carries the statement's cancellation token.
    /// </summary>
    internal bool HasDomainChecks { get; }

    /// <summary>
    /// Gets whether an application registered a cast: its code a <c>CAST</c> runs, which never sees
    /// the statement's cancellation token, but whose cancellation the engine tells apart from the
    /// statement's own by that token, so the statement's evaluator must carry it.
    /// </summary>
    internal bool HasApplicationCasts { get; }

    /// <summary>Gets the number of types: the built-in storage types and the domains.</summary>
    public int Count => _types.Length;

    /// <summary>Gets the casts, in registration order: the built-in casts first.</summary>
    public IReadOnlyList<SqlCast> Casts => Array.AsReadOnly(_casts);

    /// <summary>
    /// Finds a type by the name DDL and <c>CAST</c> write, without arguments: a built-in type name or
    /// alias (<c>INT</c>, <c>VARCHAR</c>, <c>TIMESTAMPTZ</c>), or a domain's name, ignoring case.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The type, or null when the catalog has none of the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public SqlType? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (SqlTypeNames.TryResolve(name, out DatabaseType storage))
        {
            return SqlType.ForStorage(new DatabaseTypeInfo(storage));
        }

        return _domains.TryGetValue(name, out var domain) ? domain : null;
    }

    /// <summary>
    /// Finds the cast registered from one type to another: the registry's entry itself, without the
    /// fallbacks a <c>CAST</c> applies (a domain's base type's cast, or no cast between types of one
    /// storage type).
    /// </summary>
    /// <param name="source">The source type.</param>
    /// <param name="target">The target type.</param>
    /// <returns>The cast, or null when none is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is null.</exception>
    public SqlCast? FindCast(SqlType source, SqlType target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return _castsByKey.TryGetValue(CastKey(source, target), out var cast) ? cast : null;
    }

    /// <summary>Returns the types in registration order: the built-in types, then the domains.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<SqlType> GetEnumerator() => ((IEnumerable<SqlType>)_types).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Finds a domain by name, ignoring case.</summary>
    /// <param name="name">The name.</param>
    /// <param name="domain">The domain, when the catalog registers one of the name.</param>
    /// <returns><see langword="true"/> when the catalog registers the domain.</returns>
    internal bool TryGetDomain(string name, out SqlType domain) => _domains.TryGetValue(name, out domain!);

    /// <summary>
    /// Resolves a type as DDL or <c>CAST</c> writes it, with its arguments: a built-in type name
    /// first (<c>VARCHAR(100)</c>, <c>DECIMAL(18, 4)</c>), then a domain (<c>email</c>), which takes no
    /// arguments.
    /// </summary>
    /// <param name="text">The type as written.</param>
    /// <param name="error">Why a name the catalog knows cannot be used as written; null otherwise.</param>
    /// <returns>The type, or null when the name is unknown or <paramref name="error"/> is set.</returns>
    internal SqlType? Resolve(string text, out string? error)
    {
        error = null;
        if (!SqlTypeRules.TrySplit(text, out string name, out int? first, out int? second))
        {
            error = SqlTypeModifiers.ArgumentShape;
            return null;
        }

        error = SqlTypeModifiers.GetError(name, first, second);
        if (error is not null)
        {
            return null;
        }

        if (SqlTypeRules.TryResolveBuiltIn(name, first, second, out var storage))
        {
            return SqlType.ForStorage(storage);
        }

        if (!_domains.TryGetValue(name, out var domain))
        {
            return null;
        }

        return domain;
    }

    /// <summary>
    /// The casts a <c>CAST</c> to a target may apply, by the storage type of the value it converts
    /// (<see cref="ResolveCast"/> for each storage type). A slot without a cast converts only a value
    /// of the target's own storage type, unchanged.
    /// </summary>
    /// <param name="sourceDomain">The domain the operand is known to be of, or null.</param>
    /// <param name="target">The target type.</param>
    /// <returns>The casts, one slot per storage type.</returns>
    internal SqlCast?[] ResolveCasts(SqlType? sourceDomain, SqlType target)
    {
        var casts = new SqlCast?[storageTypeCount];
        string targetKey = TypeKey(target);
        string? baseKey = target.IsDomain ? StorageKey(target.Storage.Type) : null;
        for (int storage = 1; storage < casts.Length; storage++)
        {
            casts[storage] = ResolveCast(sourceDomain, (DatabaseType)storage, target, targetKey, baseKey);
        }

        return casts;
    }

    /// <summary>
    /// The cast a <c>CAST</c> to a target applies to a value of one storage type: for a source domain
    /// known while planning, a cast from it; then a cast from the value's storage type; and for a
    /// domain target, a cast to its base type when none to the domain is registered. A value of a
    /// domain cast to that same domain takes none: PostgreSQL reduces both sides to the base type and
    /// relabels the value (<c>src/backend/parser/parse_coerce.c</c>, <c>find_coercion_pathway</c>), so
    /// the domain's own casts to and from its base type never run on it.
    /// </summary>
    /// <param name="sourceDomain">The domain the operand is known to be of, or null.</param>
    /// <param name="storage">The storage type of the value converted.</param>
    /// <param name="target">The target type.</param>
    /// <returns>The cast, or null when none applies.</returns>
    internal SqlCast? ResolveCast(SqlType? sourceDomain, DatabaseType storage, SqlType target)
        => ResolveCast(sourceDomain, storage, target, TypeKey(target), target.IsDomain ? StorageKey(target.Storage.Type) : null);

    private SqlCast? ResolveCast(SqlType? sourceDomain, DatabaseType storage, SqlType target, string targetKey, string? baseKey)
    {
        if (sourceDomain is not null && sourceDomain.Storage.Type == storage)
        {
            if (target.IsDomain && string.Equals(sourceDomain.Name, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string domainKey = TypeKey(sourceDomain);
            if ((Lookup(domainKey, targetKey) ?? (baseKey is null ? null : Lookup(domainKey, baseKey))) is { } fromDomain)
            {
                return fromDomain;
            }
        }

        string storageKey = StorageKey(storage);
        return Lookup(storageKey, targetKey) ?? (baseKey is null ? null : Lookup(storageKey, baseKey));
    }

    /// <summary>The identity of a cast's source and target: a domain by name, a built-in type by storage type.</summary>
    /// <param name="source">The source type.</param>
    /// <param name="target">The target type.</param>
    /// <returns>The key.</returns>
    internal static string CastKey(SqlType source, SqlType target) => TypeKey(source) + "|" + TypeKey(target);

    private SqlCast? Lookup(string source, string target)
        => _castsByKey.TryGetValue(source + "|" + target, out var cast) ? cast : null;

    // A cast's identity ignores length, precision and scale, as pg_cast's does: the engine applies them.
    private static string TypeKey(SqlType type)
        => type.IsDomain ? SqlObjectIdentity.Domain(type.Name.ToUpperInvariant()) : StorageKey(type.Storage.Type);

    private static string StorageKey(DatabaseType type) => "B:" + ((int)type).ToString(CultureInfo.InvariantCulture);
}
