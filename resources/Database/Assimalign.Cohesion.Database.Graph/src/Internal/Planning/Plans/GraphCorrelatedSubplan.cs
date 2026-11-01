using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A planned inner stream and its lexical argument contract, shared by semi apply and collection consumers.</summary>
internal sealed record GraphCorrelatedSubplan(GraphPlan Plan, IReadOnlyList<string> CorrelatedVariables,
    IReadOnlyDictionary<string, GraphProjectionKind> PatternArguments, GqlExpression? Parent);
