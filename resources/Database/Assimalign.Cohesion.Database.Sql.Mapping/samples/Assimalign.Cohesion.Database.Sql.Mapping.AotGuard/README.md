# SQL mapper NativeAOT guard

This executable deploys generated compiled-table declarations, starts a real SQL server over
the in-memory connection transport, and exercises the SQL mapper through its wire client.
`GuardSchema.Declare` supplies the retained C# declaration at compile time. The executable uses
the generated `SchemaTable` properties at runtime, so deployment and mapping share that declaration
without runtime reflection or expression compilation.

Publish and run from the repository root on Windows ARM64 with the NativeAOT C++ build prerequisites
(use `win-x64` on an x64 machine):

```powershell
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql.Mapping/samples/Assimalign.Cohesion.Database.Sql.Mapping.AotGuard/Assimalign.Cohesion.Database.Sql.Mapping.AotGuard.csproj -c Release -r win-arm64 -o _out/phase27-aot
& ./_out/phase27-aot/Assimalign.Cohesion.Database.Sql.Mapping.AotGuard.exe
```

If NativeAOT tool discovery reports that `vswhere.exe` is missing, add the installed Visual Studio
Installer directory to the current shell's `PATH` before publishing. On the validated machine,
that directory is `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.

The guard verifies foreign-key graph insertion and round-trip, typed predicates and pagination,
scalar and binary change tracking, one mixed insert/update/delete save, rollback after a later
foreign-key violation, retry after definite rollback, and retained schema ownership.
