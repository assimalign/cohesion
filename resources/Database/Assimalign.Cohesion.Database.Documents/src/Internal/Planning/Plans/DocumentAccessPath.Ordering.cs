using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Analysis only: Input remains the executor's selected, identity-ordered access.
internal sealed record DocumentOrderingPath(DocumentAccessPath Input,
    IReadOnlyList<DocumentOrderedIndexCandidate> Candidates) : DocumentAccessPath;

internal sealed record DocumentOrderedIndexCandidate(DocumentIndexPath Path, DocumentOrderEligibility Ordering);

// A future consumer must finish each equal ORDER BY prefix bucket, including trailing
// physical components, then emit its documents in ordinal identity order before paging.
internal sealed record DocumentOrderEligibility(bool Eligible, string Reason, bool Reverse = false,
    int EqualityKeys = 0, int OrderPrefixKeys = 0);
