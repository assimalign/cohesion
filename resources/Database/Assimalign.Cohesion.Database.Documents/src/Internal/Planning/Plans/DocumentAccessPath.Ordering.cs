using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Input preserves the ordinary access choice for fallback/intersection execution.
internal sealed record DocumentOrderingPath(DocumentAccessPath Input,
    IReadOnlyList<DocumentOrderedIndexCandidate> Candidates, DocumentOrderedIndexCandidate? Consumed) : DocumentAccessPath;

internal sealed record DocumentOrderedIndexCandidate(DocumentIndexPath Path, DocumentOrderEligibility Ordering);

// Consumption finishes each equal ORDER BY prefix bucket, including trailing
// physical components, then emits its documents in ordinal identity order before paging.
internal sealed record DocumentOrderEligibility(bool Eligible, string Reason, bool Reverse = false,
    int EqualityKeys = 0, int OrderPrefixKeys = 0);
