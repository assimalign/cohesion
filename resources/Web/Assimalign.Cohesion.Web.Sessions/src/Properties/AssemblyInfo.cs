using System.Runtime.CompilerServices;

// Friend access for the Web session test project so it can drive the internal
// middleware, feature, and id generator directly. The Cohesion repo is not
// strong-name signed in CI builds, so the declaration deliberately omits a
// PublicKey clause to avoid CS0281 in Release.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Web.Sessions.Tests")]
