# Assimalign.Cohesion.Content.Exe

Executable metadata inspection (PE, ELF and Mach-O) and separate, shell-free process invocation.
See [overview](docs/OVERVIEW.md) and [design](docs/DESIGN.md) for scope, limits and lifecycle contracts.

Run tests from the repository root:

```powershell
dotnet test libraries/Content/Assimalign.Cohesion.Content.Exe/tests/Assimalign.Cohesion.Content.Exe.Tests.csproj
```

The test build supplies its own non-packable console fixture and launches it through the running
.NET installation's host. No downloaded corpus or local package feed is needed. A fresh worktree
requires the shared build tasks; if they are missing, run
`dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj` first.
