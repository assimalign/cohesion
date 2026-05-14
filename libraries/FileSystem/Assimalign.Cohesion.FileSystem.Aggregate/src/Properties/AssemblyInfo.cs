using System.Runtime.CompilerServices;

// Friend access for the Aggregate test project. The repo isn't strong-name signed in CI so
// the declaration deliberately omits a PublicKey clause (see PR2's CS0281 fix).
[assembly: InternalsVisibleTo("Assimalign.Cohesion.FileSystem.Aggregate.Tests")]
