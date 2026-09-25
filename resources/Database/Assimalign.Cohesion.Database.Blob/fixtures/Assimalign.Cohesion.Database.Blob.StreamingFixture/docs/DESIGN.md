# Blob.StreamingFixture — Design

The Blob tests build this executable with `ReferenceOutputAssembly=false` and
copy its output beside the test assembly. The executable references the Blob
engine and accesses persistent databases through its public API. This keeps test
processes tied to the current source build while avoiding runtime reflection.

The diagram shows these build references; every arrow means references.

```mermaid
flowchart LR
    Tests["Database.Blob.Tests"] --> Fixture["Blob.StreamingFixture"]
    Fixture --> Engine["Database.Blob"]
```

The large-object mode runs with a 64 MiB GC heap hard limit and asserts that
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` is below the 128 MiB plus 123-byte
payload. One 64 KiB buffer generates bytes by their position and feeds an
incremental SHA-256 digest. Independent readback hashes both the live and reopened
content. Reopening uses a file set copied before engine disposal: the journal
still contains the complete write and is asserted larger than the payload. This
also proves that recovery can scan a WAL exceeding the process's memory budget.
Buffer size stays fixed as the content grows; the process cannot hide a
whole-object array within its memory allowance.

The crash mode commits one blob before creating an unfinished replacement and an
unfinished new blob. Separate databases let both incomplete writes remain active
under the engine's shared transaction locks. WAL flush, page write-back, and
checkpoint passes run before the readiness signal. The parent kills the whole
process tree at that signal, ensuring that no stream disposal, rollback, or engine
flush can repair the files before recovery. The parent then verifies the committed
digest and the absence of the uncommitted metadata.

Both parent tests have finite timeouts and forcibly stop surviving children in
their cleanup paths. The executable is never part of the runtime framework or
release inventory.
