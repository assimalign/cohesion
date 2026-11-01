namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Large-search evidence does not enlarge exact or ordinary plans.</summary>
internal sealed record SqlHeuristicJoinPlan : SqlSearchedJoinPlan
{
    internal SqlHeuristicJoinPlan(SqlJoinPlan selected, int states, long pairs, long evaluations,
        double boundCost, double candidateCost, double boundMemory, double candidateMemory,
        string order, bool reordered, bool sourceOnly, bool accepted)
        : base(selected, states, 0, accepted ? candidateCost : boundCost, order, reordered, sourceOnly)
    {
        ComponentPairs = pairs;
        CostEvaluations = evaluations;
        BoundCost = boundCost;
        CandidateCost = candidateCost;
        BoundMemory = boundMemory;
        CandidateMemory = candidateMemory;
        Accepted = accepted;
    }

    internal long ComponentPairs { get; }
    internal long CostEvaluations { get; }
    internal double BoundCost { get; }
    internal double CandidateCost { get; }
    internal double BoundMemory { get; }
    internal double CandidateMemory { get; }
    internal bool Accepted { get; }
}
