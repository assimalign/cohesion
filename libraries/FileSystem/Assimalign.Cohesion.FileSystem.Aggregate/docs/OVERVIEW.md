# Assimalign.Cohesion.FileSystem.Aggregate

## Summary

`IFileSystem` implementation that composes multiple mounted providers
under a single virtual namespace. Operations are routed to the mount with
the longest matching path prefix. Useful for layering — InMemory for
`/cache`, Physical for `/data`, IsolatedStorage for `/userprefs`, all
behind one `IFileSystem`.

## When to pick it

- Apps that need multiple storage backings exposed as one tree.
- Composing read-only seed data (mounted at one path) with a writable
  scratch area (mounted at another).
- Test fixtures that overlay an in-memory writable layer on top of a
  read-only physical fixture.

## Public surface

| Type | Role |
|------|------|
| `AggregateFileSystem` | The provider. |
| `AggregateFileSystemOptions` | `Name`, `IsReadOnly`, internal mount table. |
| `AggregateFileSystemBuilder` | Single-use fluent builder — `Mount`, `WithName`, `AsReadOnly`, `Build`. |
| `FileSystemFactoryBuilder.AddAggregateFileSystem` | Factory-builder extension. |

Internal routing primitives (`AggregateMount`, `AggregateRouter`,
synthetic directory, fan-in event token) are under `src/Internal/`.

## Routing rules

- **Longest-prefix wins.** Given mounts `/data` and `/data/cache`, the
  request `/data/cache/x` resolves to the `/data/cache` mount.
- **Segment-boundary check.** `/data` does not match `/database` — the
  next char after the mount path must be `/` or end-of-path.
- **Synthetic intermediates.** Path segments that lead toward a mount but
  aren't themselves a mount root surface as read-only synthetic
  directories. `Exists("/data") == true` even when only `/data/cache` is
  mounted.
- **Mutating ops in synthetic space.** Reject with
  `FileSystemException(ReadOnly)` — the aggregate doesn't synthesize new
  mounts on the fly.

## Cross-provider Copy / Move

- **Same provider** on both sides: delegate to the underlying provider
  (preserves timestamps and any optimizations).
- **Different providers**: stream the source's bytes through `Open(Read)`
  into a freshly-created destination on the target provider. The source
  is left in place until the destination write completes, so a mid-copy
  failure doesn't lose data.

## Path remapping

Returned `IFileSystemFile` / `IFileSystemDirectory` instances are wrapped
so their `Path` lives in aggregate-space (`/data/foo.txt`) instead of
provider-relative form (`/foo.txt`). The `FileSystem` property points
back at the aggregate, not the underlying mount.

## Watch fan-in

`AggregateFileSystem.Watch(pattern)` returns a fan-in token that
subscribes to every mount's watch token, remaps the event path back into
aggregate-space, and only then applies the supplied glob filter. The fan-
in token tracks the underlying subscriptions and disposes them all when
itself is disposed.

## Disposal ownership

Each mount is registered with an `ownsFileSystem` flag (default `false`).
Aggregate `Dispose()` cascades only to owned mounts; externally-managed
providers are left alone. `DisposeAsync()` follows the same rule.

## Test coverage

77 tests including:

- 34 contract tests from `FileSystemStandardTests` against an aggregate
  with InMemory mounted at `/` (exercises the wrapper layer + standard
  contract simultaneously).
- 18 router unit tests covering longest-prefix, segment-boundary,
  synthetic intermediates, child enumeration, normalization, and the
  bidirectional path-translation theories.
- 25 aggregate-specific tests covering builder validation, nested mount
  priority, cross-provider Copy/Move, dispose ownership, watch fan-in,
  factory registration.
