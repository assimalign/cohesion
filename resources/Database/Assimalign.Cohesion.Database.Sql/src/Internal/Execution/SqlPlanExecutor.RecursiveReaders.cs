using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Count single-pass scans before opening any reader. Unopened scans still need
    // the prefix; nested-loop and correlated rescans conservatively keep history.
    private static Dictionary<SqlDerivedRelation, int> RecursiveReaderCounts(SqlPlan plan)
    {
        var counts = new Dictionary<SqlDerivedRelation, int>(ReferenceEqualityComparer.Instance);
        var definitions = new HashSet<SqlDerivedRelation>(ReferenceEqualityComparer.Instance);
        Visit(plan, false);
        return counts;

        void Visit(SqlPlan current, bool rescan)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            switch (current)
            {
                case SqlWithPlan with: Visit(with.Input, rescan); break;
                case SqlSequenceScopePlan sequence: Visit(sequence.Input, rescan); break;
                case SqlSubqueryPlan subquery:
                    Visit(subquery.Input, rescan);
                    foreach (var child in subquery.Queries) { Visit(child.Query, rescan || child.IsCorrelated); }
                    break;
                case SqlCorrelatedSubqueryPlan subquery:
                    Visit(subquery.Input, rescan);
                    foreach (var child in subquery.Queries) { Visit(child.Query, rescan || child.IsCorrelated); }
                    break;
                case SqlRecursiveUnionPlan recursive:
                    Visit(recursive.Anchor.Query, rescan); Visit(recursive.Recursive.Query, true); break;
                case SqlJoinPlan join:
                    VisitJoin(RecursiveJoinTree(join), rescan);
                    break;

                    void VisitJoin(SqlJoinTree node, bool repeats)
                    {
                        RuntimeHelpers.EnsureSufficientExecutionStack();
                        if (node.Step is { } step)
                        {
                            VisitJoin(node.Left!, repeats);
                            // FULL buffers its right input once before reading the
                            // left. Other right inputs reopen for each left row.
                            VisitJoin(node.Right!, repeats || step.Kind != SqlJoinType.FullOuter);
                            return;
                        }
                        if (join.Bindings[node.Binding].Source.Derived is not { IsRecursiveReference: false } relation) { return; }
                        repeats |= relation.IsCorrelated;
                        if (relation.IsCommonTable || relation.CatalogView is not null)
                        {
                            counts.TryGetValue(relation, out int readers);
                            counts[relation] = repeats || readers < 0 ? -1 : readers + 1;
                            // Each view reference owns a separate runtime namespace;
                            // its inner reader counts belong to that scope alone.
                            if (relation.CatalogView is null && definitions.Add(relation)) { Visit(relation.Query, false); }
                        }
                        else { Visit(relation.Query, repeats); }
                    }
                case SqlGroupPlan group: Visit(group.Input, rescan); break;
                case SqlWindowAggregatePlan window: Visit(window.Input, rescan); break;
                case SqlSetOperationPlan set:
                    foreach (var operand in set.Operands) { Visit(operand.Query, rescan); }
                    break;
                case SqlInsertSelectPlan insert: Visit(insert.Source, rescan); break;
                case SqlCreateTableAsPlan { Source: { } source }: Visit(source, rescan); break;
                case SqlUpdatePlan { Source: { } source }: Visit(source.Associations, true); break;
                case SqlDeletePlan { Source: { } source }: Visit(source.Associations, true); break;
            }
        }
    }
}
