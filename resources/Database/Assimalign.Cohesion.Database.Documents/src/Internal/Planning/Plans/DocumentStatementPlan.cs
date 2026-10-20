using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// The logical stage validates document semantics; the physical stage binds a
// collection and chooses candidate retrieval without changing those semantics.
internal abstract record DocumentStatementPlan;
