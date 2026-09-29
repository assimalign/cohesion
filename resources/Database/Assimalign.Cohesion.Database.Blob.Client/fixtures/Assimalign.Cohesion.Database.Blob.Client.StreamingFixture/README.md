# Blob wire streaming fixture

The Blob.Client process test builds and copies this executable automatically. It
runs a file-backed Blob engine, generic server and typed client together over
Connections.InMemory with a 64 MiB managed heap cap. A generated 256 MiB + 123 byte
nonseekable source uploads through the wire and downloads into incremental SHA-256
verification; neither direction has a whole-object buffer.

The sole argument is a fresh database root directory. Set
`DOTNET_GCHeapHardLimit=0x4000000` and `DOTNET_GCServer=0`. The executable fails if
the effective available heap exceeds 64 MiB. Success prints the transferred length,
available heap, independently verified SHA-256 digest and maximum source read size.

The fixture can be published as NativeAOT with
`-p:BlobClientFixtureNativeAot=true`; it has no reflection or runtime type discovery.
