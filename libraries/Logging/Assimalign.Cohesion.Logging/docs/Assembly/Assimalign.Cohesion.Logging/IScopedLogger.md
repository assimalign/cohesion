# `Assimalign.Cohesion.Logging.IScopedLogger`

Disposable logger bound to a parent scope. Entries written through the scoped logger
inherit the scope's `ParentId` as their `ILoggerEntry.ParentId` unless the entry author
supplied a `ParentId` explicitly.

## Properties

| Property | Description |
| --- | --- |
| `ParentId` | Id of the seed entry that opened the scope. |

## Inherited members

`IScopedLogger : ILogger`. See `ILogger.md` for `IsEnabled`, `Log`, `BeginScope`.

## Lifecycle

- Obtained from `ILogger.BeginScope(seed)`.
- Disposable; calling `Dispose` more than once is a no-op.
- Disposed scopes throw `ObjectDisposedException` on `Log`.
- Nesting: `scope.BeginScope(innerSeed)` opens a child scope whose `ParentId` is
  `innerSeed.Id`. The inner seed itself is stamped with the outer scope's `ParentId`.

## Provider failures

When a provider throws while opening the underlying scope, the composite substitutes a
no-op scope so the parent composite stays usable.
