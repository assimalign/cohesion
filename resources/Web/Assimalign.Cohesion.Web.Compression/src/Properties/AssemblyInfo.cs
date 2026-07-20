using System.Runtime.CompilerServices;

// Friend access for the Web test project. The Cohesion repo is not strong-name signed in
// CI builds, so the declaration deliberately omits a PublicKey clause to avoid CS0281 in Release.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Web.Compression.Tests")]
