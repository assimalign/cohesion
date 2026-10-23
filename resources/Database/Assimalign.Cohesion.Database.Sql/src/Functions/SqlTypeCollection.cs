using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The SQL types and casts an engine under construction knows: its builder's
/// <see cref="SqlDatabaseEngineBuilder.Types"/>, with the built-in types and the built-in casts
/// already registered, the casts through the same <see cref="AddCast"/> an application calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>One catalog for built-in types and domains</b> (phase E3, owner decision 68 of 2026-10-09).
/// DDL and <c>CAST</c> resolve a built-in type name first and fall back to this catalog for any other
/// name, so a domain an application adds with <see cref="Add"/> is named in SQL exactly as a built-in
/// type is. A domain is engine-wide: every database of the engine sees it, as every database sees
/// the engine's functions (owner decision 61). Physical base types, collations and operators stay
/// closed in this iteration.
/// </para>
/// <para>
/// <b>What <see cref="Add"/> refuses</b>: a type that is not a domain (the built-in types are
/// registered already), and a domain whose name another registered domain has, ignoring case.
/// <b>What <see cref="AddCast"/> refuses</b>: a cast to or from a domain that is not registered in
/// this collection, and a second cast of the same source and target, a built-in's included, so a
/// built-in cast cannot be replaced (the standard library registers first). The rules are the same
/// for every registrant: the standard library's casts pass through them as an application's do, and
/// an application may add a cast between two built-in types the standard library leaves out
/// (<c>DOUBLE</c> to <c>TEXT</c>, say), as PostgreSQL's <c>CREATE CAST</c> checks only who owns the
/// types (<c>src/backend/commands/functioncmds.c</c>). Both refuse any call once the engine's build
/// began. There is no removal.
/// </para>
/// <para>
/// The engine's build freezes the collection into the engine's <see cref="SqlTypeCatalog"/>
/// (<see cref="SqlDatabaseEngine.Types"/>), together with the domains its declared schemas declare.
/// Like the builder, the collection is not thread-safe.
/// </para>
/// </remarks>
public sealed class SqlTypeCollection : IReadOnlyCollection<SqlType>
{
    private readonly List<SqlType> _types = new(SqlType.BuiltIn);
    private readonly List<SqlCast> _casts = [];
    private readonly HashSet<SqlCastKey> _castKeys = [];
    private readonly Action? _ensureMutable;

    /// <summary>Initializes a collection holding the built-in types and casts.</summary>
    /// <param name="ensureMutable">Throws once the owner no longer accepts registrations; null for none.</param>
    internal SqlTypeCollection(Action? ensureMutable)
    {
        _ensureMutable = ensureMutable;
        foreach (var type in _types)
        {
            SqlDatabaseEventSource.Log.ExtensionRegistered(type);
        }
        SqlStandardCasts.Register(this);
    }

    /// <summary>Gets the number of types: the built-in storage types and the registered domains.</summary>
    public int Count => _types.Count;

    /// <summary>Gets the registered casts, in registration order: the built-in casts first.</summary>
    public IReadOnlyList<SqlCast> Casts => _casts.AsReadOnly();

    /// <summary>Registers a domain.</summary>
    /// <param name="type">The domain (<see cref="SqlType.CreateDomain"/>).</param>
    /// <returns>This collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a domain.</exception>
    /// <exception cref="InvalidOperationException">The engine's build began, or a domain of the same name is registered.</exception>
    public SqlTypeCollection Add(SqlType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _ensureMutable?.Invoke();
        if (!type.IsDomain)
        {
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCollectionAddArgument, type.Name), nameof(type));
        }

        if (Find(type.Name) is { } registered)
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCollectionAddFailure, type.Name, (ReferenceEquals(registered, type) ? "it is" : $"domain '{registered.Name}' is")));
        }

        _types.Add(type);
        SqlDatabaseEventSource.Log.ExtensionRegistered(type);
        return this;
    }

    /// <summary>
    /// Registers a cast: between two built-in types the standard library does not convert, or to or
    /// from a registered domain.
    /// </summary>
    /// <param name="cast">The cast (<see cref="SqlCast.Create"/>).</param>
    /// <returns>This collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cast"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The engine's build began; a domain it names is not registered in this collection; or a cast
    /// of the same source and target is registered, a built-in one included.
    /// </exception>
    public SqlTypeCollection AddCast(SqlCast cast)
    {
        ArgumentNullException.ThrowIfNull(cast);
        _ensureMutable?.Invoke();
        foreach (var end in (ReadOnlySpan<SqlType>)[cast.Source, cast.Target])
        {
            if (end.IsDomain && !ReferenceEquals(Find(end.Name), end))
            {
                throw new InvalidOperationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCollectionAddCastFailure, cast, end.Name));
            }
        }

        if (!_castKeys.Add(SqlTypeCatalog.CastKey(cast.Source, cast.Target)))
        {
            bool builtIn = _casts.Exists(registered => SqlStandardCasts.Contains(registered)
                && SqlTypeCatalog.CastKey(registered.Source, registered.Target).Equals(SqlTypeCatalog.CastKey(cast.Source, cast.Target)));
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCollectionAddCastFailure2, cast.Source.Name, cast.Target.Name, (builtIn ? " by the standard library" : string.Empty)));
        }

        _casts.Add(cast);
        SqlDatabaseEventSource.Log.ExtensionRegistered(cast);
        return this;
    }

    /// <summary>
    /// Whether a type of a name is known, ignoring case: a built-in type name or alias
    /// (<c>INT</c>, <c>VARCHAR</c>, <c>TIMESTAMPTZ</c>), or a registered domain's name, as the frozen
    /// catalog's <see cref="SqlTypeCatalog.Find"/> resolves one.
    /// </summary>
    /// <param name="name">The SQL name, for example <c>BIGINT</c> or <c>email</c>.</param>
    /// <returns><see langword="true"/> when the collection has the type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SqlTypeNames.TryResolve(name, out _) || Find(name) is { IsDomain: true };
    }

    /// <summary>Returns the types in registration order: the built-in types, then the domains.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<SqlType> GetEnumerator() => _types.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Adds the domains a declared schema declares (<c>schema.Type&lt;T&gt;</c>), after the build began:
    /// phase 2 of the engine's build, before it freezes the catalog. A domain the application
    /// registered already, of the same name and base type, is the same domain and is kept.
    /// </summary>
    /// <param name="domain">The declared domain.</param>
    /// <returns>Null when the domain was added or is registered already; otherwise why it conflicts.</returns>
    internal string? AddDeclared(SqlType domain)
    {
        if (Find(domain.Name) is not { } registered)
        {
            _types.Add(domain);
            SqlDatabaseEventSource.Log.ExtensionRegistered(domain);
            return null;
        }

        return registered.IsDomain && registered.BaseType!.Storage.Type == domain.Storage.Type
            && registered.Storage.MaxLength == domain.Storage.MaxLength
            && registered.Storage.Precision == domain.Storage.Precision
            && registered.Storage.Scale == domain.Storage.Scale
                ? null
                : $"it declares domain '{domain.Name}' over {domain.BaseType!.Name}, but the engine registers " +
                  (registered.IsDomain ? $"domain '{registered.Name}' over {registered.BaseType!.Name}" : $"the built-in type {registered.Name}");
    }

    /// <summary>Freezes the registrations into the catalog the engine executes; phase 2 of the build.</summary>
    /// <returns>The catalog.</returns>
    internal SqlTypeCatalog Freeze() => new(_types, _casts);

    private SqlType? Find(string name)
    {
        foreach (var type in _types)
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return null;
    }
}
