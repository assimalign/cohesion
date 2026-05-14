using System.Runtime.CompilerServices;

// Friend access for the Dns test project. The Cohesion repo is not strong-name signed in CI
// builds, so the declaration deliberately omits a PublicKey clause to avoid CS0281 in Release
// (see the IsolatedStorage rename PR for the same fix).
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Dns.Tests")]
