# Assimalign.Cohesion.Database.Storage — Design

The physical layer of the Data Platform kernel (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.2). This document records the design
decisions that shape the storage model; the program-level requirements it satisfies are
R1 (ACID), R3 (shared kernel), and R6 (NativeAOT) in the area design.

## Design intent

One model-agnostic physical substrate: fixed-size pages, a pin-counted buffer pool, a
slotted-page record layout, and a write-ahead journal. Model engines bring *layouts*
(what bytes mean inside a page body) but never their own paging, caching, or logging.
The guardrails that keep this layer trustworthy are structural: every page load is
checksum-verified, every write-back is checksum-stamped, and durability flows through
the journal only — there are no side files.

## The page model

- **8 KiB pages, 96-byte header.** The header layout (`Page.Header`) is an explicit
  struct persisted on disk, so field offsets are a wire contract: id (0), LSN (8),
  checksum (16), flags (20), type (21), slot count (22), free-data end (24), overflow
  size (28), then reserved nonce/MAC space for encryption at rest (32–63).
- **`PageType.Free` is zero — deliberately.** A zero-initialized page reads as free,
  and `FreePage` re-stamps freed pages with `Free`, which is what lets the free-space
  map be *reconstructed from page headers* on open instead of being persisted as a
  separate structure that could drift from reality. The trade-off: a page freed but not
  yet flushed at process exit reappears as allocated after reopen — a safe leak, never
  corruption. (A bitmap FSM page type is reserved for when file sizes make the open-time
  scan matter.)
- **Page LSN.** Every page carries the LSN of the journal record that last modified it.
  This is the hook for the write-ahead rule (a page may not be written to the data
  stream until the journal is durable up to its LSN) and for idempotent recovery replay
  (apply a record only if it is newer than the page). The storage layer stores the
  field; the journal build-out (#160) enforces the rule.
- **Checksums on every read path.** CRC-32 over the full page with the checksum field
  zeroed. Stamped centrally in the buffer pool's write-back (the only path to the data
  stream), verified centrally in the buffer pool's load (the only path from it).
  A stored checksum of zero means "never stamped" and skips verification — accepted
  because the alternative (a validity bit elsewhere) buys nothing against the ~2⁻³²
  false-negative rate this already carries.

### Why the file header lives in the page body

`StorageFileHeader` (magic, format version, storage id/name, page counts, checkpoint
LSN) sits in the **body** of page 0 — after the standard 96-byte page header — not at
file offset 0. An earlier draft overlaid the file header on the page header, which made
page 0 un-checksummable and un-typed. Making page 0 a normal `PageType.FileHeader` page
means one integrity rule covers every page in the file, including the header.

## The buffer pool

Pin-counting with RAII handles (`IStoragePageHandle`): a page cannot be evicted while
pinned, dirty pages are written back (checksum-stamped) before eviction, and handles
release their pin on dispose. Contrast with a `Memory<byte>`-pooling design: pages are
*pinned* GC handles exposing raw pointers because the slotted-page and header structs
are `unsafe` overlays — the pool guarantees pointer stability for the handle's lifetime.

- **Eviction is least-recently-used** over unpinned entries: pins and cache hits move a
  page to the MRU end; capacity overflow evicts from the LRU end, skipping pinned
  pages, and fails loudly (`StorageIOException`) when every resident page is pinned —
  the pool never silently exceeds its memory budget. LRU (not clock/2Q) because the
  pool is fully lock-serialized anyway, so the precise policy costs nothing extra and
  is trivially testable.
- **Buffers are reused**: evicted entries return their pinned 8 KiB buffers (GC handle
  and all) to a recycle stack, so steady-state page churn performs zero allocations —
  fresh pages are zeroed on reuse so recycled buffers never leak prior content.
- **Failed loads never poison the cache**: a page that fails checksum verification is
  not cached; its buffer goes straight back to the recycle stack and the exception
  propagates.
- **One lock.** All pool state is guarded by a single monitor. Page *content* access is
  the caller's concern (a handle hands out a raw pointer); the transaction layer above
  provides content-level isolation. Sharding the lock is a measured-need optimization,
  not a default.

## The record layer

`SlottedPage` implements the classic slotted layout: records grow forward from the
header, the slot directory grows backward from the page end, deletion marks a slot
(length 0) and `Compact` defragments. All four models share this because their unit of
storage — row, document, KV entry, node/edge record — is "a variable-length byte
sequence addressed by (page, slot)". Records above `SlottedPage.MaxRecordSize` are
rejected at the API boundary; multi-page records ride overflow pages (a later feature —
the flags and page type are reserved).

## The journal (write-ahead log)

`IStorageJournal` is the durability mechanism — the *only* one. Frames are length-prefixed,
magic-tagged, and CRC-protected; a torn or corrupted tail terminates the read scan and
is ignored — it belongs to work that was never acknowledged. Records are typed and
binary (begin / commit / rollback / checkpoint / before-image / after-image / opaque
logical operation); transaction identity at this level is a compact monotonic `long`
sequence — GUID identity belongs to the transaction layer above.

### Write ordering rules (steal / no-force, full page images)

1. **Before-image at first touch.** A transaction's first modification of a page
   appends the page's full prior image and stamps the pooled page's LSN with that
   record — mutations then apply in the buffer pool only.
2. **The write-ahead gate.** The buffer pool may steal (evict) a dirty page at any
   time, but its write-back first forces the journal durable up to the page's LSN —
   so any uncommitted content that reaches the data file is always undoable from a
   durable before-image.
3. **Commit = after-images + commit record + fsync.** Commit appends the after-image
   of every touched page (stamping each page's LSN with its record), then the commit
   record, and acknowledges only after `EnsureDurable(commitLsn)`. Data pages are
   *not* forced — recovery redoes them (no-force).
4. **Rollback restores in memory.** Before-images are kept per transaction and copied
   back into the pooled pages, so rollback is complete without I/O; a rollback record
   marks the outcome.
5. **Page-level single-writer.** A page touched by an active transaction is
   write-locked to it (conflicts throw rather than wait). Record-level concurrency is
   `Database.Transactions`' job above this layer; full-image logging is only correct
   because two transactions can never interleave on one page. This division is
   permanent in the MVCC integration design (area DESIGN.md §3.8): storage
   transactions remain the **physical WAL bracket** — the MVCC manager layers
   row-grain snapshots/locks *above* them (paired per transaction via
   `IStorageTransactionSource`), and page locks stop being the user-visible
   conflict surface without ever weakening the invariant that makes page-image
   logging correct.

Full page images (8 KiB per touch) were chosen over byte-range deltas deliberately:
they make recovery a pure idempotent overwrite with no operation replay logic, which
is the property the crash suites verify. Deltas are a measured-need optimization that
can ride the same record types later.

### Recovery replay rules

Because images are full pages and pages are single-writer, the desired final state of
a page is the image of the **last** journal record on it among *committed
after-images* and *uncommitted before-images* — redo and undo collapse into one
last-record-wins pass. Replay is idempotent by exact-LSN match: an after-image stamps
its record LSN, a before-image restores the pre-transaction LSN embedded in the
captured bytes, and an image is skipped only when the on-disk page already verifies
(checksum) at exactly the target LSN. Recovery runs on open, writes directly to the
data stream (bypassing the pool — a corrupt page must be overwritable), and finishes
with a checkpoint.

### Checkpoints

`Checkpoint()` durably flushes all page state and **truncates** the journal, writing a
fresh checkpoint record whose LSN continues the sequence (LSNs never restart — page
LSN comparisons depend on monotonicity across truncation). Checkpointing requires no
active transactions — truncating live before-images would orphan stolen writes; fuzzy
checkpoints are a later feature (the record already carries the active-transaction
set). Clean shutdown checkpoints, so a clean reopen recovers instantly.

With a background checkpointer (#902) checkpoints race live transactions, so the
emptiness check hardened from "no page write locks" to an **active-transaction count**
taken in `BeginTransaction` under the transaction lock and released exactly once per
commit/rollback: a begun-but-untouched transaction holds no page lock yet has already
appended its begin record, and the whole checkpoint now runs *under* the transaction
lock, so no transaction can slip between the emptiness check and the truncation.
(Lock order is transaction lock → buffer pool → journal; no path takes them in
reverse.) A checkpoint attempted while transactions are active still throws
`StorageTransactionException` — background checkpointers treat that as "busy, retry
next pass".

### Commit durability modes (group commit)

`Storage.CommitDurability` selects who performs the commit's durable flush — never
whether it happens:

- **`Synchronous` (default):** commit calls `EnsureDurable(commitLsn)` inline — one
  fsync per commit, simplest latency profile.
- **`Grouped`:** commit registers its LSN on the internal group-commit gate, wakes
  the engine's flush worker through the `OnCommitPending` hook, and waits. The worker
  calls `IStorage.FlushPendingCommits()` — one durable flush covering the highest
  pending LSN — and wakes every covered committer, so concurrent commits share one
  fsync. **Self-help invariant:** a committer not woken within `GroupCommitWindow`
  flushes inline itself; a missing, stalled, or misconfigured worker costs bounded
  latency, never durability. A commit is acknowledged only after its records are
  durable in either mode.

The gate lives in storage (not the engine) because commit blocks inside
`CommitTransaction`; the engine contributes only the worker loop and the wake signal.
Page write locks release after the durability wait, exactly as in synchronous mode.

### Paced page write-back

`IStorage.WriteBackDirtyPages(maxPages)` writes back a bounded batch of dirty
buffered pages without evicting them — the page-writer worker's pass between
checkpoints, so a checkpoint's `FlushAll` does not spike. Every write-back path (this
one, eviction, `FlushAll`) funnels through the buffer pool's single write-back
routine, so the write-ahead gate (journal durable ≥ page LSN) holds for stolen pages
here exactly as everywhere else.

### What is deliberately unlogged

Page 0 (the file header) carries only recomputable bookkeeping and is rebuilt or
revalidated on open; it is flushed but never journaled. Page allocation is likewise
not undone on rollback — a page allocated by an aborted transaction is restored to
its empty initialized image and leaks safely until reused.

## Error model

`StorageException` is the area root for this library. `StorageIOException` (stream and
allocation failures), `SlottedPageException` (record layout violations),
`StorageCorruptionException` (checksum/header integrity failures — carries the
`PageId`), and `JournalException` (journal framing/state violations) all derive from
it, so consumers can catch the family or the specific failure.

## AOT posture

No reflection, no runtime codegen. Header structs are explicit-layout overlays read
through pointers; encodings are hand-written span code. `AllowUnsafeBlocks` is enabled
for the pointer overlays — the unsafe surface is confined to `Units/` and the buffer
pool's pinned buffers.

## Non-goals

- **No model semantics.** Nothing here knows what a row or document is.
- **No distributed I/O.** One storage instance = one file set on one machine.
  Replication rides the journal from `Database.Replication`, not this layer.
- **No encryption yet.** The header reserves nonce/MAC space and `PageFlags.Encrypted`;
  the encryption-at-rest feature (#861) implements it beneath the buffer pool.
