using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// The base of the graph model's requests: a parsed, database-scoped GQL statement and its
/// parameter values. A graph session executes either leaf through its one typed execute member.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a base.</b> The graph model has two request kinds that carry the same statement and run
/// through the same session: <see cref="GraphQueryRequest"/> returns rows and
/// <see cref="GraphPathsQueryRequest"/> returns paths. The base is the session's request root
/// (<c>DatabaseLanguageSession&lt;GraphRequest&gt;</c>), so both leaves reach the typed member and a
/// request of another model does not compile. The constructor fixes the result shape; the session
/// reads that state without testing the request's concrete type.
/// </para>
/// <para>
/// <b>Shape.</b> Both leaves live in this assembly, so the constructor is <c>private protected</c>
/// (database-area.md, rule 3). The statement and the parameters are fixed at construction and
/// field-backed (rule 6).
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class GraphRequest : DatabaseRequest
{
    private readonly bool _paths;
    private readonly GqlQueryStatement _statement;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>
    /// Initializes a graph request over a parsed GQL statement.
    /// </summary>
    /// <param name="statement">The parsed statement.</param>
    /// <param name="parameters">The parameter values, or null when the statement binds none.</param>
    /// <param name="paths">Whether the request returns paths instead of rows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="statement"/> is null.</exception>
    private protected GraphRequest(GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, bool paths = false)
    {
        ArgumentNullException.ThrowIfNull(statement);
        _paths = paths;
        _statement = statement;
        _parameters = parameters;
    }

    /// <summary>
    /// Gets the parsed GQL statement.
    /// </summary>
    public GqlQueryStatement Statement => _statement;

    /// <summary>Gets whether this request returns paths instead of rows.</summary>
    internal bool Paths => _paths;

    /// <summary>The wire Execute exchange requires statically scalar result columns.</summary>
    internal bool ScalarsOnly { get; init; }

    internal bool CompositeValues { get; init; } = true;

    /// <summary>
    /// Gets the named scalar, list or map bindings for $name and @name references, or null for none.
    /// Lookup strips one leading sigil from map keys and uses ordinal, case-sensitive bare names.
    /// Unbound references fail planning with COHDBG001; duplicate normalized keys use COHDBG014.
    /// Referenced values retain their runtime scalar types; unsupported values use COHDBG003.
    /// Lists snapshot to <c>object?[]</c>; maps to read-only <c>IReadOnlyDictionary&lt;string, object?&gt;</c>.
    /// Values permit depth 64, 65,536 total values including the root, and 16 MiB of encoded bytes.
    /// Pattern property maps remain scalar-only. Wire composites require the negotiated capability;
    /// the client refuses composite parameters locally without sending a frame when it is absent.
    /// Unused entries are ignored.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Parameters => _parameters;
}
