# Assimalign.Cohesion.Content.Exe — Design

## Design intent

Reserve the Content family's executable-file abstraction on top of `Content.Binary`.
The current surface is scaffolding: `IExecutableFile : IBinaryFile` declares
`InvokeAsync()` returning `ExecutableResult`, and the result and `Class1` types are empty.
This is not a working executable parser or execution service.

## Design choices and error model

The binary-file seam keeps format concerns within the Content family instead of adding
a process-host dependency. Execution ownership and a useful result/error contract still
need design; no implementation currently defines invocation or format-failure behavior.
Future format failures should follow the [Content design](../../Assimalign.Cohesion.Content/docs/DESIGN.md).

## Lifecycle and known constraints

H2 (`4592e3eb`) restored the build. The single test is an empty placeholder, so a passing
suite provides no execution or parsing assurance. There is no implemented resource lifecycle.
Legacy namespaces and incomplete public documentation remain outside this documentation pass.

## AOT posture and non-goals

The project inherits `IsAotCompatible=true`; the current scaffold introduces no dynamic-code
implementation. This is not evidence of a NativeAOT-published executable engine. Process
launching, PE/ELF parsing, sandboxing, and executable rewriting are not delivered here.
