using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Source-ordered leaves and indexed binary nodes preserve grouping without recursive cursors.
internal sealed record GraphCompositePlan(GqlQueryExpression Query, GqlCompositeOperator Operator,
    IReadOnlyList<GraphPlan> Operands, IReadOnlyList<Diagnostic> Warnings, GraphFunctionKind[] ColumnKinds, IReadOnlyList<GraphProjectionPlan> Projections, IReadOnlyList<GraphBindingShape> Shapes,
    IReadOnlyList<GraphCompositeNode> Nodes, bool HomogeneousUnion);
