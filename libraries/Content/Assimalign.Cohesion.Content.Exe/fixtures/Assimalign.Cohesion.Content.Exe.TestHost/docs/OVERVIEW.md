# Executable test host

This non-packable console harness is built by the Content.Exe test project. Tests launch
`dotnet Assimalign.Cohesion.Content.Exe.TestHost.dll <mode> <arguments>` using the running runtime's
host. Modes echo arguments as UTF-8/base64 lines, write stdout/stderr, copy stdin, report environment
and working directory, sleep, exit with a code, or create a three-process tree. No downloaded files
or shell are involved. See [DESIGN.md](DESIGN.md) for cleanup and output discovery.
