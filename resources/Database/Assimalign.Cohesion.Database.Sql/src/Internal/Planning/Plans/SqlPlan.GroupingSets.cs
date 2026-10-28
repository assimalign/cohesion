namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Keeps extension-only metadata out of plain GROUP BY plans and execution frames.</summary>
internal sealed record SqlGroupingSetsPlan : SqlGroupPlan
{
    internal SqlGroupingSetsPlan(SqlGroupPlan plan, int[][]? groupingSets, int[][]? groupingArguments)
        : base(plan)
    {
        GroupingSets = groupingSets;
        GroupingArguments = groupingArguments;
    }

    internal int[][]? GroupingSets { get; }
    internal int[][]? GroupingArguments { get; }
}
