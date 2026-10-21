using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Transactions;


/// <summary>
/// The result of journal analysis: which transactions committed, which aborted, and
/// the highest sequence observed (the floor for new sequence assignment).
/// </summary>
/// <param name="Committed">Sequences with a durable commit record.</param>
/// <param name="Aborted">Sequences seen in the journal without a durable commit record.</param>
/// <param name="MaxSequence">The highest sequence observed, or zero when the journal is empty.</param>
public sealed record TransactionRecoveryPlan(
    IReadOnlySet<TransactionSequence> Committed,
    IReadOnlySet<TransactionSequence> Aborted,
    TransactionSequence MaxSequence);
