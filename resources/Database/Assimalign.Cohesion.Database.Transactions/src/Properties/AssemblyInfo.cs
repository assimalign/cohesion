using System.Runtime.CompilerServices;

// The tests reach this assembly's internals for five purposes:
// - the deferred-undo retry schedule, driven by their own clock (TransactionCoordinator's
//   internal TimeProvider constructor, DeferredUndoBackoff);
// - the internal TransactionLog base and its CreateInMemory/CreateJournalBound factories, which
//   the fault-injecting logs (FailingCommitLog, ControlledLog) derive from and the recovery tests
//   call (#1257);
// - the internal TransactionManager.Create(TransactionLog, ...) overload that installs those logs
//   (#1257);
// - the coordinator's BeforeCheckpoint, SequenceReserved and BeforeAbortRecord hooks, which
//   replaced the deleted IStorage/IStorageJournal test doubles (#1257);
// - the private protected VersionStore constructor, which the fault-injecting
//   ControlledVersionStore derives through (#1258).
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Transactions.Tests")]
