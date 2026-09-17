# Documents recovery fixture

This nonpackable test executable exercises document persistence, nested JSON,
secondary indexes, and OQL after abrupt process termination. It also provides
a representative consumer for NativeAOT publishing. It is a fixture of the
Documents test project, not a shipping application or hosting integration.

Run `seed <directory>` in an empty directory, then `verify <directory>` in another
process. Seed exits without disposing its engine after committed and uncommitted
write brackets. Verify asserts that only the committed data and index membership
survive.

NativeAOT validation uses `dotnet publish -c Release -r win-x64
-p:DocumentFixtureNativeAot=true` on the fixture project. The fixture sets
`PublishAot` locally so it does not propagate into the repository's netstandard
source-generator projects.
