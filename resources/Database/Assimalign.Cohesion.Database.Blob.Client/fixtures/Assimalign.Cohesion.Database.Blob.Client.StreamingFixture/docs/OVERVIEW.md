# Blob wire streaming fixture

This non-packable executable proves that Blob.Client and BlobDatabaseServer can
round-trip an object four times larger than their shared process's available GC
heap. It depends on Database.Blob.Client, Database.Blob and Connections.InMemory.
Blob.Client's process test supplies the heap cap and an isolated file storage root.

Run the executable with one database-root argument and a 64 MiB
`DOTNET_GCHeapHardLimit`. No package feed or external service is needed.

NativeAOT verification uses the fixture-local `BlobClientFixtureNativeAot=true`
property so build-time source generators remain managed. Publish with a short
`--artifacts-path` on Windows to avoid native symbol paths exceeding 260 characters:

```powershell
dotnet publish resources/Database/Assimalign.Cohesion.Database.Blob.Client/fixtures/Assimalign.Cohesion.Database.Blob.Client.StreamingFixture/Assimalign.Cohesion.Database.Blob.Client.StreamingFixture.csproj -c Release -r win-x64 -p:BlobClientFixtureNativeAot=true --artifacts-path _out/a9 -o _out/phase9-blob-wire-aot
```
