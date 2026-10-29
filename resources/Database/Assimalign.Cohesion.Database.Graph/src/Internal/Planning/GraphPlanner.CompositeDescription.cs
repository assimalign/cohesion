using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphPlanner
{
    internal DatabasePlanDescription Describe(GraphCompositePlan plan, GraphPlanExecutor.ExpansionBudget? budget = null)
    {
        var nodes = new List<DatabasePlanNode>();
        AppendCompositeDescription(plan, nodes, null, budget);
        return DatabasePlanDescription.Create(nodes, plan.Warnings);
    }

    private void AppendCompositeDescription(GraphCompositePlan plan, List<DatabasePlanNode> nodes, int? parent,
        GraphPlanExecutor.ExpansionBudget? budget)
    {
        var pending = new Stack<(GqlQueryExpression Query, int? Parent)>();
        pending.Push((plan.Query, parent));
        int operand = 0;
        while (pending.TryPop(out var step))
        {
            if (step.Query.Composite is { } composite)
            {
                int id = nodes.Count;
                nodes.Add(new DatabasePlanNode
                {
                    NodeId = id, ParentId = step.Parent,
                    Operator = composite.Operator == GqlCompositeOperator.Union ? "Union" : "UnionAll",
                    Detail = $"read_only=True; shared_snapshot=True; shared_budget=True; distinct={composite.Operator == GqlCompositeOperator.Union}; memory_refusal={(composite.Operator == GqlCompositeOperator.Union ? "CG05030" : "CG05020")}",
                });
                pending.Push((composite.Right, id));
                pending.Push((composite.Left, id));
            }
            else { AppendDescription(plan.Operands[operand++], nodes, step.Parent, budget); }
        }
    }
}
