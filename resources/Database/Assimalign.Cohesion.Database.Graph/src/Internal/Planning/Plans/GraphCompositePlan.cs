using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Homogeneous UNION trees can stream their leaves in source order; the query retains grouping.
internal sealed record GraphCompositePlan(GqlQueryExpression Query, GqlCompositeOperator Operator,
    IReadOnlyList<GraphPlan> Operands, IReadOnlyList<Diagnostic> Warnings, GraphFunctionKind[] ColumnKinds, IReadOnlyList<GraphProjectionPlan> Projections, IReadOnlyList<GraphBindingShape> Shapes);
