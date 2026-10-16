# Assimalign.Cohesion.Content.Exe

Content.Exe provides independent executable metadata inspection and process invocation APIs.
It targets .NET 10 and depends on Content.Binary (and transitively Content).

## Inspect bytes

Use `new ExecutableReader().Read(stream)` or `ReadAsync(stream, cancellationToken)` to detect and
inspect PE images, ELF or Mach-O. The stream must be readable and seekable; the file starts at its
current position, and the caller retains ownership. Use a per-format reader when the format is known.
`ExecutableReader.Detect(prefix)` returns a descriptor or `ContentFormat.Unknown` without executing
anything. Reader options bound table counts and metadata memory.

The returned `IExecutableFile` is detached metadata with format, machine, bitness, byte order, kind,
entry point where retained, and format-specific header/table models. It does not own source bytes
or expose invocation. Malformed bytes produce `ContentFormatException` with a code and absolute
position. Unknown formats can be detected without error; inspecting them reports `Unsupported`.

## Invoke a path

Create `ExecutableStartInfo` with the executable path and individual arguments, then pass it to
`new ProcessExecutableInvoker().InvokeAsync(startInfo, cancellationToken)`. Working directory,
environment overrides/removals, standard input, output byte caps and timeout are explicit options.
The implementation uses no shell. Output drains concurrently and capture respects each cap.

Results distinguish exit, timeout, cancellation and failed start, with exit code when available,
UTF-8 stdout/stderr, truncation flags and timing. Cancellation and timeout clean up the process tree
before returning. An invocation owns its process and redirected handles until completion.

Inspection never authorizes invocation. The invoker accepts a path, never in-memory executable
content; callers own temporary-file execution policy. See [DESIGN.md](DESIGN.md) for O-C1, the exact
scope and specifications, lifecycle details, limits and extension seams.
