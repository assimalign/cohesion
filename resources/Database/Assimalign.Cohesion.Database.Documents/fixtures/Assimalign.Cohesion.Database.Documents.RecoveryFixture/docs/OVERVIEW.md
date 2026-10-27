# Documents recovery fixture

This nonpackable test executable exercises document persistence, nested JSON,
secondary indexes (including a composite UNIQUE key), and OQL after abrupt process termination. It also provides
a representative consumer for NativeAOT publishing. It is a fixture of the
Documents test project, not a shipping application or hosting integration.

Run `seed <directory>` in an empty directory, then `verify <directory>` in another
process. Seed exits without disposing its engine after committed and uncommitted
write brackets. Verify asserts that only the committed data and index membership
survive.

NativeAOT validation uses `dotnet publish -c Release -r win-arm64
-p:DocumentFixtureNativeAot=true` on the fixture project. The fixture sets
`PublishAot` locally so it does not propagate into the repository's netstandard
source-generator projects.

## Registered functions

The same executable also validates typed scalar/aggregate registration and a derived
NULL/MISSING-aware scalar under NativeAOT. Both processes register the functions in code;
verify executes them over recovered documents. The command line and storage format are unchanged.
