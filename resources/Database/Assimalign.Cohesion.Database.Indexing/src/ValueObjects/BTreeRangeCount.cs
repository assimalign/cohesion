namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>A bounded, snapshot-visible range count used for transient planning estimates.</summary>
/// <param name="Count">Visible entries encountered; a lower bound when incomplete.</param>
/// <param name="IsComplete">Whether the entire requested range was examined.</param>
/// <param name="EntriesExamined">Physical entries examined, including invisible versions.</param>
/// <param name="PagesVisited">Page visits, including the descent and empty leaves.</param>
public readonly record struct BTreeRangeCount(long Count, bool IsComplete, int EntriesExamined, int PagesVisited);
