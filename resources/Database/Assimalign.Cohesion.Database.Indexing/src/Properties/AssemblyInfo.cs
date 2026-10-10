using System.Runtime.CompilerServices;

// The tests reach this assembly's internals for two purposes: the internal IndexEventSource, whose
// name, manifest and events its tests check (event-source.md, "Tests"), and a tree's latch and root
// page, which the rollback ordering test holds and reads as a cursor does (#1371). Granted to this
// project's own test assembly only (general-rules.md, "InternalsVisibleTo is for tests").
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Indexing.Tests")]
