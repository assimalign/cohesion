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
                    Operator = composite.Operator.ToString(),
                    Detail = $"read_only=True; shared_snapshot=True; shared_budget=True; distinct={composite.Operator is GqlCompositeOperator.Union or GqlCompositeOperator.Except or GqlCompositeOperator.Intersect}; " +
                        (composite.Operator == GqlCompositeOperator.Otherwise ? "fallback=whole_result; lazy_right=True; errors_propagate=True; memory_refusal=CG05020" :
                         composite.Operator is GqlCompositeOperator.Except or GqlCompositeOperator.ExceptAll or GqlCompositeOperator.Intersect or GqlCompositeOperator.IntersectAll
                            ? $"multiplicity={(composite.Operator == GqlCompositeOperator.ExceptAll ? "max(m-n,0)" : composite.Operator == GqlCompositeOperator.IntersectAll ? "min(m,n)" : "distinct")}; memory_refusal=CG05032"
                            : $"memory_refusal={(composite.Operator == GqlCompositeOperator.Union ? "CG05030" : "CG05020")}"),
                });
                pending.Push((composite.Right, id));
                pending.Push((composite.Left, id));
            }
            else { AppendDescription(plan.Operands[operand++], nodes, step.Parent, budget); }
        }
    }
}
