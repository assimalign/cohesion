# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4070 files · ~1,195,436 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 298 nodes · 535 edges · 19 communities (13 shown, 6 thin omitted)
- Extraction: 87% EXTRACTED · 13% INFERRED · 0% AMBIGUOUS · INFERRED: 69 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `0f7a6f19`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- Storage
- StorageModelAlignmentTests
- SlottedPage
- StorageStream
- Assimalign.Cohesion.Database.Storage
- StorageBufferPool
- BufferEntry
- StoragePageManager
- Crc32
- Assimalign.Cohesion.Database.Storage — Design
- 4. The work items (with blockers)
- StorageFreeSpaceMap
- Assimalign.Cohesion.Database.Indexing — Design
- StorageFileHeader
- Assimalign.Cohesion.Database.Storage.Tests.csproj
- byte
- int
- IStoragePageHandle
- PageId

## God Nodes (most connected - your core abstractions)
1. `Storage` - 33 edges
2. `StorageBufferPool` - 22 edges
3. `StorageStream` - 21 edges
4. `StorageBufferPoolTests` - 15 edges
5. `StoragePageManager` - 15 edges
6. `Assimalign.Cohesion.Database.Storage` - 13 edges
7. `SlottedPage` - 13 edges
8. `StorageFreeSpaceMap` - 12 edges
9. `StorageUnitIterator` - 12 edges
10. `StorageModelAlignmentTests` - 11 edges

## Surprising Connections (you probably didn't know these)
- `StoragePageManager` --references--> `StorageBufferPool`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StoragePageManager.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StorageBufferPool.cs
- `Storage` --references--> `StorageBufferPool`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Storage.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StorageBufferPool.cs
- `Storage` --references--> `StorageFreeSpaceMap`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Storage.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StorageFreeSpaceMap.cs
- `StoragePageManager` --references--> `StorageStream`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StoragePageManager.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/StorageStream.cs
- `Storage` --references--> `StoragePageManager`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Storage.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Internal/StoragePageManager.cs

## Import Cycles
- None detected.

## Communities (19 total, 6 thin omitted)

### Community 0 - "Storage"
Cohesion: 0.10
Nodes (19): IJournalLogger, IStorage, Name, Storage, bool, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageHandle (+11 more)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.18
Nodes (8): StorageModelAlignmentTests, TestStorage, PageId, ReadOnlyMemory, SlotIndex, StorageModel, Stream, TestStorage

### Community 2 - "SlottedPage"
Cohesion: 0.10
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.12
Nodes (11): Memory, StorageStream, CancellationToken, PageId, ReadOnlyMemory, ReadOnlySpan, Span, ValueTask (+3 more)

### Community 4 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.08
Nodes (16): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Internal, Assimalign.Cohesion.Database.Storage.Units, Assimalign.Cohesion.Database.Storage.Tests, JournalException, StorageCorruptionException, PageId, StorageFileHeader (+8 more)

### Community 5 - "StorageBufferPool"
Cohesion: 0.14
Nodes (13): BufferEntry, Dictionary, Fact, IStorageBufferPool, IStoragePageHandle, LinkedList, object, PageId (+5 more)

### Community 6 - "BufferEntry"
Cohesion: 0.22
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.11
Nodes (12): HashSet, IStorageFreeSpaceMap, IStoragePageManager, Queue, StorageFreeSpaceMap, long, PageId, StoragePageManager (+4 more)

### Community 8 - "Crc32"
Cohesion: 0.20
Nodes (7): Crc32, ReadOnlySpan, uint, PageChecksum, PageId, ReadOnlySpan, Span

### Community 9 - "Assimalign.Cohesion.Database.Storage — Design"
Cohesion: 0.12
Nodes (14): AOT posture, Assimalign.Cohesion.Database.Storage — Design, Design intent, Error model, Non-goals, The buffer pool, The journal, The page model (+6 more)

### Community 10 - "4. The work items (with blockers)"
Cohesion: 0.14
Nodes (13): 1. How to run this across multiple sessions (read first), 2. Stages (dependency gates), 3. Lanes (what can run in parallel) + per-lane guardrails, 4. The work items (with blockers), 5. Progress Log (orchestrator-reconciled from merged PRs), 6. Fast reference, Database Program Plan (Data Platform, L03.02), MVP definition (what "done" means for the first cut) (+5 more)

### Community 11 - "StorageFreeSpaceMap"
Cohesion: 0.22
Nodes (7): bool, byte, GCHandle, int, LinkedListNode, Page, BufferEntry

### Community 12 - "Assimalign.Cohesion.Database.Indexing — Design"
Cohesion: 0.22
Nodes (8): AOT posture, Assimalign.Cohesion.Database.Indexing — Design, Byte-comparable keys, Entry references are opaque `ulong`s, Intent, Non-goals, Relationship to `Database.Storage`, Transactional binding

## Knowledge Gaps
- **31 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+26 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **6 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Storage` connect `Storage` to `StorageModelAlignmentTests`, `SlottedPage`, `StorageStream`, `Assimalign.Cohesion.Database.Storage`, `StorageBufferPool`, `StoragePageManager`?**
  _High betweenness centrality (0.298) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Storage` to `StorageFreeSpaceMap`, `StoragePageManager`?**
  _High betweenness centrality (0.142) - this node is a cross-community bridge._
- **Why does `StorageBufferPool` connect `StorageBufferPool` to `Storage`, `StorageFreeSpaceMap`, `StoragePageManager`?**
  _High betweenness centrality (0.139) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _31 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Storage` be split into smaller, more focused modules?**
  _Cohesion score 0.0962566844919786 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10098522167487685 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.1225071225071225 - nodes in this community are weakly interconnected._