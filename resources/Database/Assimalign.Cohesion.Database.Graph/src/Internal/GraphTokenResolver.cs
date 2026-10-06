using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Resolves the label and relationship-type names a statement reads against the catalog at the
/// statement's snapshot, each name once, and records a warning for every name the database does
/// not have (#1228). A read never fails on an unknown name: no node carries a label the catalog
/// lacks and no relationship has such a type, so the name simply matches nothing, as in Neo4j,
/// whose token resolution leaves an unresolved name out of the semantic table without an error
/// (<c>cypher-planner/.../compiler/planner/ResolveTokens.scala:80-105</c>) and whose
/// <c>CheckForUnresolvedTokens</c> phase reports each one as a notification
/// (<c>CheckForUnresolvedTokens.scala:61-88</c>).
/// </summary>
/// <remarks>
/// The warnings are coded diagnostics of <see cref="DiagnosticSeverity.Warning"/> severity:
/// <c>COHDBG010</c> for a label (Neo4j's <c>Neo.ClientNotification.Statement.UnknownLabelWarning</c>,
/// GQLSTATUS 01N50) and <c>COHDBG011</c> for a relationship type
/// (<c>UnknownRelationshipTypeWarning</c>, 01N51), as <c>common/.../kernel/api/exceptions/Status.java:346-355</c>
/// and <c>neo4j-notifications/.../NotificationCodeWithDescription.java:190-199</c> define them. Neo4j
/// records one notification per mention position in a set; pattern names carry no source position
/// here, so a name is reported once per kind, at its first mention, with the span of the labeled
/// predicate that names it when it is first named there.
/// </remarks>
internal sealed class GraphTokenResolver
{
    /// <summary>The warning code for a label the database does not have.</summary>
    internal const string UnknownLabelCode = "COHDBG010";

    /// <summary>The warning code for a relationship type the database does not have.</summary>
    internal const string UnknownRelationshipTypeCode = "COHDBG011";

    private readonly GraphCatalog _catalog;
    private readonly TransactionSnapshot _snapshot;
    private readonly Dictionary<string, bool> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _types = new(StringComparer.Ordinal);
    private List<Diagnostic>? _warnings;

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
    internal IReadOnlyList<Diagnostic> Warnings => _warnings is null ? [] : _warnings.AsReadOnly();

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
            if (!known) { (_warnings ??= []).Add(UnknownLabel(name, location)); }
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
            if (!known) { (_warnings ??= []).Add(UnknownRelationshipType(name, location)); }
        }
        return known;
    }

    /// <summary>Creates the <c>COHDBG010</c> warning for a label the database does not have.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="location">The source span that names it, or <see langword="null"/>.</param>
    /// <returns>The warning.</returns>
    internal static Diagnostic UnknownLabel(string name, Location? location) => Create(UnknownLabelCode,
        $"Label '{GraphLabelEvaluator.Shorten(name)}' is not in the database, so no node carries it. " +
        "Check its spelling, or that the label exists when the statement runs.", location);

    /// <summary>Creates the <c>COHDBG011</c> warning for a relationship type the database does not have.</summary>
    /// <param name="name">The relationship type name.</param>
    /// <param name="location">The source span that names it, or <see langword="null"/>.</param>
    /// <returns>The warning.</returns>
    internal static Diagnostic UnknownRelationshipType(string name, Location? location) => Create(UnknownRelationshipTypeCode,
        $"Relationship type '{GraphLabelEvaluator.Shorten(name)}' is not in the database, so no relationship has it. " +
        "Check its spelling, or that the type exists when the statement runs.", location);

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
}
