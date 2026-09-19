# Mapping NativeAOT guard

This executable uses the database generator's output from the retained `SqlSchema.Compile`
declaration in `GuardSchema.cs`. It checks generated reader/writer round-trips, typed keys,
identity, detached scalar and binary snapshots, atomic rollback, retries and deletion.

The project explicitly enables `CohesionGenerateDatabaseMappers=true` and exposes the property
to the generator with `CompilerVisibleProperty`. Mapper generation is opt-in so schema-only
applications are unaffected when the analyzer is delivered by the Database framework.

The executable never invokes the schema declaration. The existing schema builder performs
expression and CLR type inspection when authoring schemas; the mapping runtime consumes only
the statically emitted mapper. The generator tests separately execute a real schema and compare
its compiled column order with the generated materializer.

From the repository root on Windows ARM64 with the NativeAOT C++ toolchain installed
(use `win-x64` on an x64 host):

```powershell
dotnet publish resources/Database/Assimalign.Cohesion.Database.Mapping/samples/Assimalign.Cohesion.Database.Mapping.AotGuard/Assimalign.Cohesion.Database.Mapping.AotGuard.csproj -c Release -r win-arm64 -o _out/phase26-aot -nodeReuse:false
& ./_out/phase26-aot/Assimalign.Cohesion.Database.Mapping.AotGuard.exe
```

Successful execution prints:

```text
Mapping NativeAOT guard passed: round-trip, identity, scalar/binary tracking, atomic rollback, retry and delete.
```

Publishing alone does not pass this guard: run the native executable and require exit code zero.
