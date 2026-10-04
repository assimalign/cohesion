using System.Runtime.CompilerServices;

// The tests drive the deferred-undo retry schedule with their own clock
// (TransactionCoordinator's internal TimeProvider constructor, DeferredUndoBackoff).
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Transactions.Tests")]
