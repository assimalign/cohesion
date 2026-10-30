using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Resolves the label, relationship-type and entity-property names a statement reads against the catalog at the
/// statement's snapshot and records one warning per name the database does
/// not have (#1228, #1249). A read never fails on an unknown name: no node carries a label the catalog
/// lacks and no relationship has such a type, so the name simply matches nothing, as in Neo4j,
/// whose token resolution leaves an unresolved name out of the semantic table without an error
/// (<c>cypher-planner/.../compiler/planner/ResolveTokens.scala:80-105</c>) and whose
/// <c>CheckForUnresolvedTokens</c> phase reports each one as a notification
/// (<c>CheckForUnresolvedTokens.scala:61-88</c>).
/// </summary>
/// <remarks>
/// The warnings are coded diagnostics of <see cref="DiagnosticSeverity.Warning"/> severity:
/// <c>CG01010</c> for a label (Neo4j's <c>Neo.ClientNotification.Statement.UnknownLabelWarning</c>,
/// GQLSTATUS 01N50) and <c>CG01011</c> for a relationship type
/// (<c>UnknownRelationshipTypeWarning</c>, 01N51), as <c>common/.../kernel/api/exceptions/Status.java:346-355</c>
/// and <c>neo4j-notifications/.../NotificationCodeWithDescription.java:190-199</c> define them. Neo4j
/// records one notification per mention position in a set; pattern names carry no source position
/// here, so a name is reported once per kind, at its first mention, with the span of the labeled
/// predicate that names it when it is first named there.
/// Entity properties use <c>CG01023</c>, once per ordinal name across the read-only statement;
/// callers prove the receiver is an entity before checking the database-wide property directory.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("GraphTokenResolver")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTokenResolver.DebugView))]
internal sealed class GraphTokenResolver : IDisposable
{
    private static readonly ConcurrentBag<TokenWarnings> _propertyStates = new();
    private static readonly string _errorUnknownRelationshipTypeDiagnostic = Resources.UnknownRelationshipTypeDiagnostic;

    private static readonly string _unknownPropertyDiagnostic = Resources.UnknownPropertyDiagnostic;

    private static readonly string _errorTokenResolverUnknownLabelDiagnostic = Resources.TokenResolverUnknownLabelDiagnostic;

    /// <summary>The warning code for a label the database does not have.</summary>
    internal const string UnknownLabelCode = "CG01010";

    /// <summary>The warning code for a relationship type the database does not have.</summary>
    internal const string UnknownRelationshipTypeCode = "CG01011";

    /// <summary>The warning code for an entity property unknown database-wide.</summary>
    internal const string UnknownPropertyCode = "CG01023";

    private readonly GraphCatalog _catalog;
    private readonly TransactionSnapshot _snapshot;
    private readonly Dictionary<string, bool> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _types = new(StringComparer.Ordinal);
    private TokenWarnings? _warnings;

    /// <summary>Initializes a new instance of the <see cref="GraphTokenResolver"/> class.</summary>
    /// <param name="catalog">The catalog whose definitions the names resolve against.</param>
    /// <param name="snapshot">The statement snapshot, so a transaction sees its own new labels and types.</param>
    internal GraphTokenResolver(GraphCatalog catalog, TransactionSnapshot snapshot)
    {
        _catalog = catalog;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Gets a warning for each unknown name, in first-mention order, or an empty list when every
    /// name resolved.
    /// </summary>
    internal IReadOnlyList<Diagnostic> Warnings => _warnings?.Items?.AsReadOnly() ?? (IReadOnlyList<Diagnostic>)[];

    internal void IntroduceLabel(string name) => _labels[name] = true;
    internal void IntroduceRelationshipType(string name) => _types[name] = true;

    /// <summary>Whether the database has the label; records its warning the first time it does not.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="location">The source span that names it, when the AST has one.</param>
    /// <returns><see langword="true"/> when a visible label has the name.</returns>
    internal bool HasLabel(string name, Location? location = null)
    {
        if (!_labels.TryGetValue(name, out bool known))
        {
            known = _catalog.FindLabel(name, _snapshot) is not null;
            _labels.Add(name, known);
            if (!known) { (State.Items ??= []).Add(UnknownLabel(name, location)); }
        }
        return known;
    }

    /// <summary>Whether the database has the relationship type; records its warning the first time it does not.</summary>
    /// <param name="name">The relationship type name.</param>
    /// <param name="location">The source span that names it, when the AST has one.</param>
    /// <returns><see langword="true"/> when a visible relationship type has the name.</returns>
    internal bool HasRelationshipType(string name, Location? location = null)
    {
        if (!_types.TryGetValue(name, out bool known))
        {
            known = _catalog.FindRelationshipType(name, _snapshot) is not null;
            _types.Add(name, known);
            if (!known) { (State.Items ??= []).Add(UnknownRelationshipType(name, location)); }
        }
        return known;
    }

    // Resolution and warning admission share one statement-local result per ordinal name.
    // Known-only state is borrowed and returned after planning, retaining the ordinary
    // resolver layout and warmed read allocations. Warning lists outlive their resolver.
    internal void CheckProperty(string name, Location? location = null)
    {
        _catalog.BeforePropertyCheck?.Invoke();
        if (HasPropertyKey(name)) { return; }
        var warnings = State;
        if (!(warnings.Reported ??= new(StringComparer.Ordinal)).Add(name)) { return; }
        (warnings.Items ??= []).Add(Create(UnknownPropertyCode,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, _unknownPropertyDiagnostic, GraphLabelEvaluator.Shorten(name)), location));
    }

    internal bool HasPropertyKey(string name)
    {
        var properties = State.Properties;
        if (!properties.TryGetValue(name, out bool known))
        {
            known = _catalog.HasPropertyKey(name, _snapshot);
            properties.Add(name, known);
        }
        return known;
    }

    private TokenWarnings State => _warnings ??= _propertyStates.TryTake(out var state) ? state : new();

    public void Dispose()
    {
        var state = _warnings;
        _warnings = null;
        // A diagnostic carrier may retain Items.AsReadOnly(); never clear or lend that state.
        // Bound retained pooled capacity when a statement mentions many distinct keys.
        if (state is null || state.Items is not null || state.Properties.Count > 1_024) { return; }
        state.Properties.Clear();
        _propertyStates.Add(state);
    }

    /// <summary>Creates the <c>CG01010</c> warning for a label the database does not have.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="location">The source span that names it, or <see langword="null"/>.</param>
    /// <returns>The warning.</returns>
    internal static Diagnostic UnknownLabel(string name, Location? location) => Create(UnknownLabelCode,
        string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorTokenResolverUnknownLabelDiagnostic, GraphLabelEvaluator.Shorten(name)), location);

    /// <summary>Creates the <c>CG01011</c> warning for a relationship type the database does not have.</summary>
    /// <param name="name">The relationship type name.</param>
    /// <param name="location">The source span that names it, or <see langword="null"/>.</param>
    /// <returns>The warning.</returns>
    internal static Diagnostic UnknownRelationshipType(string name, Location? location) => Create(UnknownRelationshipTypeCode,
        string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorUnknownRelationshipTypeDiagnostic, GraphLabelEvaluator.Shorten(name)), location);

    private static Diagnostic Create(string code, string message, Location? location) => new()
    {
        Code = code,
        Message = message,
        Severity = DiagnosticSeverity.Warning,
        Start = location?.Start,
        End = location?.End,
        Line = location?.StartLine ?? 1,
        Location = location is null ? null : DiagnosticLocation.Absolute,
    };

    [System.Diagnostics.DebuggerDisplay("TokenWarnings")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(TokenWarnings.DebugView))]
    private sealed class TokenWarnings
    {
        internal List<Diagnostic>? Items { get; set; }
        internal Dictionary<string, bool> Properties { get; } = new(StringComparer.Ordinal);
        internal HashSet<string>? Reported { get; set; }
        private sealed class DebugView { public DebugView(TokenWarnings value) { } }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTokenResolver value)
        {
            _ = value;
        }
    }
}
