# Graph NativeAOT smoke

Build `build/Tasks` in Release first. Publish this project in Release for the host RID and run
the resulting executable. On Windows ARM64:

```powershell
dotnet publish resources/Database/Assimalign.Cohesion.Database.Graph/samples/Assimalign.Cohesion.Database.Graph.AotSample -c Release -r win-arm64 -o C:/cw/w11-aot-graph
C:/cw/w11-aot-graph/Assimalign.Cohesion.Database.Graph.AotSample.exe
```

The Visual Studio Installer directory must be on PATH so ILC can locate the native linker.
The executable verifies streaming fan-out, grouping, DISTINCT, ordered pages, MATCH, paths,
budget refusals and reuse after a refused statement. Trim/AOT warnings fail publishing.
