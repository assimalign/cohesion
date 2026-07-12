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
Eviction policy is the buffer-pool build-out story (#158); the contract deliberately
does not promise an ordering, only pin-safety.

## The record layer

`SlottedPage` implements the classic slotted layout: records grow forward from the
header, the slot directory grows backward from the page end, deletion marks a slot
(length 0) and `Compact` defragments. All four models share this because their unit of
storage — row, document, KV entry, node/edge record — is "a variable-length byte
sequence addressed by (page, slot)". Records above `SlottedPage.MaxRecordSize` are
rejected at the API boundary; multi-page records ride overflow pages (a later feature —
the flags and page type are reserved).

## The journal

`IJournalLogger` is the durability mechanism — the *only* one. Frames are
length-prefixed, magic-tagged, and CRC-protected; a torn tail is detected and ignored
at read time. Commit and checkpoint force a durable flush (`FileStream.Flush(true)`).
The current record model is transaction-scoped logical operations; the journal
write-ordering and recovery-replay build-out (#160) evolves this into the ARIES-shaped
redo/undo contract described in the area design, using the page LSN field added here.

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
