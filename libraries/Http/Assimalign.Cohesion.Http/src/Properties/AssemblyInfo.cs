using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Assimalign.Cohesion.Http.Tests")]

// The Web routing host constraint (RouteHostConstraint, #788) shares the structural host[:port]
// split (HttpHost.TrySplitHostPort / TryParsePort) so route host SELECTION and host allowlist
// VALIDATION cannot drift on what a wire value means (#890). The split is internal plumbing, not
// public API; this friend grant lets the one in-repo constraint reuse it without widening surface.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Web.Routing")]
