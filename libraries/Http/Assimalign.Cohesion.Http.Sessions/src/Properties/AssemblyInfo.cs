using System.Runtime.CompilerServices;

// Friend access for the Web session middleware, which composes the internal
// store-backed session over the configured store, and for this library's own
// test project. The Cohesion repo is not strong-name signed in CI builds, so the
// declarations deliberately omit a PublicKey clause to avoid CS0281 in Release.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Web.Sessions")]
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Http.Sessions.Tests")]
