# Blob streaming acceptance fixture

This non-packable process fixture is built by `Database.Blob.Tests` through a
`CohesionProjectReference` with `ReferenceOutputAssembly=false`. Its runtime files
are copied beside the test output, so the tests launch the current source build
with the installed .NET host without reflection or assumptions about repository
location.

`roundtrip <root>` writes 128 MiB plus 123 bytes of deterministic data while the
parent imposes a 64 MiB GC heap hard limit. The fixture asserts that the runtime's
reported available memory is smaller than the blob. Upload and readback use
64 KiB buffers and incremental SHA-256. Before disposal can checkpoint the
journal, the fixture copies the complete committed file set and asserts that
its WAL is itself larger than the blob. It reopens this recovery image and
verifies the same length and digest under the same memory limit.

`crash-writer <root>` durably commits one object, starts an unfinished replacement
and an unfinished new object in separate databases, then runs WAL flush, dirty
page write-back, and checkpoint passes. Once it prints `CRASH_READY`, the parent
kills its process tree. No engine or stream disposal occurs. The parent reopens
the files and checks that the original committed bytes survive and that no new
partial object is cataloged. A finite parent timeout kills failed fixtures too.
