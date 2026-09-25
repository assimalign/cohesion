# Proposal — make `IFileSystem` usable by storage engines

**Status:** **approved 2026-09-18** · **Created:** 2026-09-18 · **Owner:** Chase Crawford
**Motivation:** let a database engine accept any `IFileSystem` — InMemory, Physical, IsolatedStorage —
instead of hardcoding `FileStream`.

> **Approved in full by the owner on 2026-09-18**, including the load-bearing decision in §2: a
> durability-aware flush that throws rather than silently degrading. Implement per the sequencing
> in §6; steps 1 and 2 land as separate commits with the existing durability suites as the gate.

---

## 1. Why the current interface cannot carry a database

`IFileSystemFile` exposes only stream opening:

```csharp
public interface IFileSystemFile : IFileSystemInfo
{
    Size Size { get; }
    FileName Name { get; }
    IFileSystemDirectory Directory { get; }
    IFileSystemEventToken Watch();
    Stream Open();
    Stream Open(FileMode fileMode);
    Stream Open(FileMode fileMode, FileAccess fileAccess);
    Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare);
    //int Read(Span<byte> buffer, long offset);          // commented out in the interface today
    //ValueTask<int> ReadAsync(Span<byte> buffer, long offset);
    //void Write(Span<byte> buffer, long offset);
    //ValueTask WriteAsync(Span<byte> buffer, long offset);
}
```

A storage engine needs two things this cannot express:

**Positional I/O.** A pager reads and writes page *N* at offset *N × pageSize*, concurrently, from
many threads. Sequential `Stream` access with a shared cursor is the wrong shape; the commented-out
members are the right shape and are exactly what is missing.

**Durable flush.** `Database.Storage` already depends on it — `flushToDisk` appears in
`StreamJournal.cs` and `StorageStream.cs`, which is `FileStream.Flush(flushToDisk: true)`. That
overload exists on `FileStream`, **not on `Stream`**. Going through `IFileSystemFile.Open()` returns
a `Stream`, so the only way to reach fsync is to downcast to `FileStream` — which defeats the
abstraction, and **silently loses durability** the moment someone passes a filesystem whose files
are not `FileStream`-backed.

**That second point is the blocking one.** Routing the write-ahead journal through today's interface
would not fail loudly; it would produce a database that reports committed writes it cannot recover.

## 2. The proposed change — one new member

`IFileSystemFile` gains a single member. **The existing `Open` overloads are untouched**, so
Configuration.{FileSystem,Ini,Json,Xml} and Web.StaticFiles are unaffected.

```csharp
/// <summary>
/// Opens the file for random-access, optionally durable I/O — the shape a storage engine needs:
/// positional reads and writes at an offset, and a flush that can be guaranteed to reach the
/// durable medium.
/// </summary>
/// <param name="fileMode">How the file is opened or created.</param>
/// <param name="fileAccess">Whether the handle reads, writes, or both.</param>
/// <param name="fileShare">How other handles may share the file.</param>
/// <returns>A handle the caller owns and must dispose.</returns>
IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare);
```

```csharp
/// <summary>
/// A random-access handle to a file. Mirrors the shape of <see cref="System.IO.RandomAccess"/>
/// over a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>, which is what a page-oriented
/// storage engine actually needs: offset-addressed I/O that is safe to issue concurrently, plus a
/// flush whose durability is knowable rather than assumed.
/// </summary>
public interface IFileSystemFileHandle : IDisposable, IAsyncDisposable
{
    /// <summary>The current length of the file, in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Whether <see cref="Flush(bool)"/> with <c>durable: true</c> can actually guarantee the data
    /// has reached durable storage. An in-memory or otherwise non-durable file system returns
    /// <see langword="false"/> rather than pretending.
    /// </summary>
    bool SupportsDurableFlush { get; }

    /// <summary>Reads into <paramref name="buffer"/> starting at <paramref name="offset"/>.</summary>
    /// <returns>The number of bytes read, which may be fewer than requested at end of file.</returns>
    int Read(Span<byte> buffer, long offset);

    /// <inheritdoc cref="Read(Span{byte}, long)"/>
    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default);

    /// <summary>Writes <paramref name="buffer"/> starting at <paramref name="offset"/>, extending the file if needed.</summary>
    void Write(ReadOnlySpan<byte> buffer, long offset);

    /// <inheritdoc cref="Write(ReadOnlySpan{byte}, long)"/>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default);

    /// <summary>Sets the file length, truncating or extending it.</summary>
    void SetLength(long length);

    /// <summary>
    /// Flushes buffered writes. When <paramref name="durable"/> is <see langword="true"/> the data
    /// must have reached durable storage when this returns.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="durable"/> is <see langword="true"/> and <see cref="SupportsDurableFlush"/>
    /// is <see langword="false"/>. Failing loudly is deliberate: a storage engine that asks for
    /// durability and silently does not get it is worse than one that cannot start.
    /// </exception>
    void Flush(bool durable);

    /// <inheritdoc cref="Flush(bool)"/>
    ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default);
}
```

### The one decision worth arguing about

**`SupportsDurableFlush` plus a throwing `Flush(durable: true)` is the load-bearing part of this
design.** Without it, swapping in an in-memory file system silently converts an ACID database into
one that loses committed transactions on restart — and every existing durability test would still
pass, because they run against a real disk.

With it, an engine configured for durable commit refuses to start on a file system that cannot
provide durability, and says why. An engine configured for in-memory or relaxed durability runs
fine. The abstraction becomes honest rather than merely uniform.

## 3. Blast radius

**Four concrete implementations** must implement `OpenHandle`:

| Implementation | Effort |
|---|---|
| `PhysicalFileSystemFile` | Small — `File.OpenHandle` + `System.IO.RandomAccess`; `SupportsDurableFlush => true` |
| `InMemoryFileSystemFile` | Small — offset I/O over its existing buffer; `SupportsDurableFlush => false` |
| `IsolatedStorageFileSystemFile` | Small — same shape as Physical, durability per the isolated store |
| `AggregateFileSystemFile` | Delegates to the resolved underlying file |

**Unaffected**, because they use only the existing `Open` overloads: `Configuration.FileSystem`,
`Configuration.Ini`, `Configuration.Json`, `Configuration.Xml`, `Web.StaticFiles`.

## 4. What it buys the Database area

- `Database.Storage` stops constructing `FileStream` directly — three call sites in
  `StreamJournal.cs` and `StorageStream.cs`.
- The `FileSystem*StorageStrategy` / `InMemory*StorageStrategy` pairs in `Database.Sql` and
  `Database.KeyValuePair` collapse into one implementation over an injectable `IFileSystem`. Blob,
  Documents, and Graph have no strategies today — they compose the kernel — so they inherit this
  for free.
- Tests can run the real storage engine against `InMemory` without a temp directory, and crash
  tests can use a filesystem that fails writes deliberately.
- A future engine could run over `IsolatedStorage` or an aggregate mount without touching the kernel.

## 5. What this proposal does **not** do

- No change to the existing `Open` overloads, so no existing consumer changes.
- No file locking primitive. Databases often want advisory locks; `FileShare` covers the current
  need and adding locking is a separate decision.
- No preallocation hint (`FileOptions`/`posix_fallocate`). Worth revisiting if page-file growth
  shows up in profiling.
- No change to `IFileSystemDirectory` or `IFileSystem` themselves.

## 6. Recommended sequencing if approved

1. Add `IFileSystemFileHandle` and the `OpenHandle` member; implement in all four file systems, with
   tests covering positional I/O, `SetLength`, and the durable-flush contract including the throw.
2. Route `Database.Storage` through it, keeping `FileStream` behavior identical for the physical
   case. Crash and recovery suites must pass **unmodified** — this is a substitution, not a
   behavior change.
3. Collapse the SQL and Key-Value storage strategies onto the injectable file system.
4. Expose the file system on each engine's options so a consumer can pass one.

Steps 1 and 2 are the risky ones and should land separately, with the existing durability suites as
the gate.
