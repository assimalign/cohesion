using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Invokes an inline query for each incoming row and joins its returned bindings.</summary>
/// <param name="Body">The inner query.</param>
/// <param name="Imports">The imported variable names; null means an omitted scope clause.</param>
/// <param name="ImportAll">Whether the scope clause is (*).</param>
/// <param name="Optional">Whether an empty invocation produces one null-extended row.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlCallStage(GqlQueryExpression Body, IReadOnlyList<string>? Imports = null,
    bool ImportAll = false, bool Optional = false, Location? Location = null) : GqlStage(Location);
