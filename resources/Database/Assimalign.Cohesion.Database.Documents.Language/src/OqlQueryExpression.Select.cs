using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A query over a collection, derived query, or expression.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlSelectExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression.DebugView))]
public sealed class OqlSelectExpression : OqlQueryExpression
{
    private readonly object _from;

    /// <summary>Initializes a query over a collection or expression source.</summary>
    /// <param name="collection">The unqualified collection name or reserved COHESION_SCHEMA system collection name; ignored when <paramref name="source"/> is supplied.</param>
    /// <param name="alias">The iteration variable, including an inferred expression-source name; null uses the collection name, or the last segment of a system source.</param>
    /// <param name="projections">The projected expressions.</param>
    /// <param name="predicate">The optional document filter.</param>
    /// <param name="groupBy">The grouping expressions.</param>
    /// <param name="having">The optional group filter.</param>
    /// <param name="orderBy">The ordering expressions.</param>
    /// <param name="isDistinct">Whether to eliminate duplicate projected values.</param>
    /// <param name="isValue">Whether to return one bare expression rather than named members.</param>
    /// <param name="limit">The optional maximum row count, as a signed integer literal or parameter.</param>
    /// <param name="offset">The optional number of rows to skip, as a signed integer literal or parameter.</param>
    /// <param name="location">The source span.</param>
    /// <param name="unnest">The ordered array bindings applied before WHERE.</param>
    /// <param name="groupAliases">The optional aliases parallel to <paramref name="groupBy"/>; null entries leave terms unnamed.</param>
    /// <param name="groupAs">The optional GROUP AS binding for the array of input binding wrappers.</param>
    /// <param name="bindings">The ordered UNNEST, JOIN and NEST bindings; null uses <paramref name="unnest"/>.</param>
    /// <param name="source">The FROM expression, including a subquery for a derived source; null selects <paramref name="collection"/>.</param>
    /// <param name="windows">The query-local named windows.</param>
    /// <param name="isAliasImplicit">Whether a supplied alias was inferred from its source rather than explicitly declared.</param>
    /// <exception cref="ArgumentNullException">A supplied window declaration is null.</exception>
    public OqlSelectExpression(string collection, string? alias, IReadOnlyList<OqlProjection> projections,
        OqlExpression? predicate, IReadOnlyList<OqlExpression> groupBy, OqlExpression? having,
        IReadOnlyList<OqlOrdering> orderBy, bool isDistinct, bool isValue, OqlExpression? limit = null,
        OqlExpression? offset = null, Location? location = null, IReadOnlyList<OqlUnnestExpression>? unnest = null,
        IReadOnlyList<string?>? groupAliases = null, string? groupAs = null, OqlExpression? source = null, IReadOnlyList<OqlSourceBindingExpression>? bindings = null,
        bool isAliasImplicit = false, IReadOnlyList<OqlWindowDefinition>? windows = null) : base(orderBy, isValue, limit, offset, location)
    {
        if (windows is not null) { foreach (var window in windows) { ArgumentNullException.ThrowIfNull(window); } }
        _from = bindings is null || !bindings.Any(binding => binding is OqlJoinExpression)
            ? source is null ? collection : source : new Sources(source is null ? collection : source, bindings);
        Alias = alias;
        IsAliasImplicit = alias is null || isAliasImplicit;
        Projections = projections;
        Predicate = predicate;
        var windowDefinitions = windows ?? (groupBy as WindowGrouping)?.Windows;
        var groupKeys = (groupBy as WindowGrouping)?.Keys ?? groupBy;
        GroupBy = groupAliases is null && groupAs is null ? groupKeys : new Grouping(groupKeys, groupAliases ?? [], groupAs);
        if (windowDefinitions is { Count: > 0 }) { GroupBy = new WindowGrouping(GroupBy, windowDefinitions); }
        Having = having;
        IsDistinct = isDistinct;
        Unnest = bindings is null ? unnest ?? [] : bindings.OfType<OqlUnnestExpression>().ToArray();
    }

    /// <summary>Gets whether duplicate projected values are eliminated.</summary>
    public bool IsDistinct { get; }

    /// <summary>Gets the collection name, or an empty string for an expression source.</summary>
    public string Collection => ((_from as Sources)?.From ?? _from) as string ?? string.Empty;
    /// <summary>Gets the FROM expression, or null for a collection source.</summary>
    public OqlExpression? Source => ((_from as Sources)?.From ?? _from) as OqlExpression;
    /// <summary>Gets all source bindings in their evaluation order.</summary>
    public IReadOnlyList<OqlSourceBindingExpression> Bindings => (_from as Sources)?.Bindings ?? Unnest;
    /// <summary>Gets the iteration variable, including an inferred expression-source name, or null for an unresolved implicit collection binding. Planner copies carry the resolved name with <see cref="IsAliasImplicit"/> set to true.</summary>
    public string? Alias { get; }
    /// <summary>Gets whether the source name is implicit rather than an explicit correlation alias.</summary>
    public bool IsAliasImplicit { get; }
    /// <summary>Gets the array bindings in expansion order.</summary>
    public IReadOnlyList<OqlUnnestExpression> Unnest { get; }
    /// <summary>Gets the projection expressions in source order.</summary>
    public IReadOnlyList<OqlProjection> Projections { get; }
    /// <summary>Gets the optional document predicate.</summary>
    public OqlExpression? Predicate { get; }
    /// <summary>Gets the grouping expressions.</summary>
    public IReadOnlyList<OqlExpression> GroupBy { get; }
    /// <summary>Gets the group-term aliases, parallel to GroupBy, or an empty list when no terms have aliases.</summary>
    public IReadOnlyList<string?> GroupAliases => (((GroupBy as WindowGrouping)?.Keys ?? GroupBy) as Grouping)?.Aliases ?? [];
    /// <summary>Gets the optional name of the array of input binding wrappers.</summary>
    public string? GroupAs => (((GroupBy as WindowGrouping)?.Keys ?? GroupBy) as Grouping)?.GroupAs;
    /// <summary>Gets the optional group predicate.</summary>
    public OqlExpression? Having { get; }

    /// <summary>Gets the query-local WINDOW declarations.</summary>
    public IReadOnlyList<OqlWindowDefinition> Windows => (GroupBy as WindowGrouping)?.Windows ?? [];

    private sealed record Sources(object From, IReadOnlyList<OqlSourceBindingExpression> Bindings);

    // Keep ordinary SELECT nodes and their allocation measurements unchanged. Optional
    // grouping metadata travels with the existing key list, including planner copies.
    private sealed class Grouping : IReadOnlyList<OqlExpression>
    {
        public Grouping(IReadOnlyList<OqlExpression> keys, IReadOnlyList<string?> aliases, string? groupAs)
        { Keys = keys; Aliases = aliases; GroupAs = groupAs; }

        public IReadOnlyList<OqlExpression> Keys { get; }
        public IReadOnlyList<string?> Aliases { get; }
        public string? GroupAs { get; }
        public int Count => Keys.Count;
        public OqlExpression this[int index] => Keys[index];
        public IEnumerator<OqlExpression> GetEnumerator() => Keys.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WindowGrouping : IReadOnlyList<OqlExpression>
    {
        public WindowGrouping(IReadOnlyList<OqlExpression> keys, IReadOnlyList<OqlWindowDefinition> windows)
        { Keys = keys; Windows = Array.AsReadOnly(windows.ToArray()); }
        public IReadOnlyList<OqlExpression> Keys { get; }
        public IReadOnlyList<OqlWindowDefinition> Windows { get; }
        public int Count => Keys.Count;
        public OqlExpression this[int index] => Keys[index];
        public IEnumerator<OqlExpression> GetEnumerator() => Keys.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression value)
        {
            _target = value;
        }

        public bool IsDistinct => _target.IsDistinct;
        public bool IsValue => _target.IsValue;
        public string Collection => _target.Collection;
        public OqlExpression? Source => _target.Source;
        public IReadOnlyList<OqlSourceBindingExpression> Bindings => _target.Bindings;
        public string? Alias => _target.Alias;
        public bool IsAliasImplicit => _target.IsAliasImplicit;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression> Unnest => _target.Unnest;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Predicate => _target.Predicate;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> GroupBy => _target.GroupBy;
        public IReadOnlyList<string?> GroupAliases => _target.GroupAliases;
        public string? GroupAs => _target.GroupAs;
        public IReadOnlyList<OqlWindowDefinition> Windows => _target.Windows;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Having => _target.Having;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Offset => _target.Offset;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
