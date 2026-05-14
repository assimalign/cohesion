using System.Runtime.CompilerServices;

// Friend access for the IsolatedStorage test project. The Cohesion repo is not strong-name signed
// in CI builds, so both assemblies have PublicKeyToken=null — including a PublicKey on this
// attribute would trip CS0281 in Release. Keep the declaration unconditional and key-free.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.FileSystem.IsolatedStorage.Tests")]
