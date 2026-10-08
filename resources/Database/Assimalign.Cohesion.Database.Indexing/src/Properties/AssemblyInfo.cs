using System.Runtime.CompilerServices;

// The tests reach this assembly's internals for one purpose: the internal IndexEventSource, whose
// name, manifest and events its tests check (event-source.md, "Tests"). Granted to this project's
// own test assembly only (general-rules.md, "InternalsVisibleTo is for tests").
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Indexing.Tests")]
