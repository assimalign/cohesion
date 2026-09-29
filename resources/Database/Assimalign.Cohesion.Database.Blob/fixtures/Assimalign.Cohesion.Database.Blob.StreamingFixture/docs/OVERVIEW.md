# Blob.StreamingFixture — Overview

This non-packable executable is an acceptance input for the Blob engine's tests.
It references `Assimalign.Cohesion.Database.Blob` through a
`CohesionProjectReference` and uses only the public engine, container, and stream
contracts. There are no reflection or hosting dependencies.

The `roundtrip` mode verifies a file-backed object larger than the process's
available managed memory. The `crash-writer` mode prepares committed and unfinished
objects, then lets the parent kill the process before any disposal. See the
[fixture usage](../README.md) and [design](DESIGN.md).

## NativeAOT validation

Publish with `dotnet publish -c Release -r win-x64 -p:BlobFixtureNativeAot=true`.
The fixture-local property enables AOT only for this executable, leaving referenced
netstandard source-generator projects as managed build tools. The normal Windows
NativeAOT C++ toolchain is required. The same `roundtrip` mode can run from the
published executable with `DOTNET_GCHeapHardLimit=4000000` (hexadecimal, 64 MiB).
