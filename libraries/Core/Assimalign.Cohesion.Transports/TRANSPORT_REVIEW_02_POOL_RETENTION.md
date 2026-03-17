# Transport Review 02

## Finding

The TCP transport needed a better answer for pinned buffer reuse than either:

- retaining every returned block forever, or
- trimming to a fixed hard cap with no notion of recent pressure.

The core problem is not just "too many retained blocks." It is that the pool should react differently when:

- traffic is active,
- traffic has recently spiked, and
- the system has gone cold and should release memory.

## Revised Direction

Move the reusable pool into `Assimalign.Cohesion.Core` as a public `System.Buffers` component and make retention policy-driven.

### Why this is better

- The memory pool becomes broadly reusable outside transports.
- The transport layer stops owning a specialized buffer primitive.
- Policy-driven retention gives us a path to adapt reuse without hard-coding transport-specific heuristics into the pool itself.

## Implemented Shape

- `AdaptiveMemoryPool` lives in Core and provides pinned fixed-size byte blocks.
- `AdaptiveMemoryPoolOptions` configures block size, trim cadence, time provider, and retention policy.
- `IAdaptiveMemoryPoolPolicy` decides how many idle blocks should be retained for the current pool snapshot.
- `AdaptiveMemoryPoolPressurePolicy` is the default policy used by TCP right now.
- TCP creates an `AdaptiveMemoryPool` per I/O queue and configures its policy from the existing transport buffer thresholds.

## Benefits

- More scalable than a simple static cap.
- More maintainable than keeping transport-specific pool logic in the transport assembly.
- Publicly consumable for other lower-level components that need the same pattern.
- Testable via manual trimming and injected time providers.

## Tradeoffs

- The public API surface is larger.
- Background trimming introduces more moving parts than a pure queue-only pool.
- Policy behavior needs careful defaults so callers are not surprised by memory release timing.

## Recommended Follow-Through

If this direction is accepted:

1. Merge the Core adaptive pool and TCP wiring.
2. Revisit whether UDP or QUIC should eventually use the same public pool abstraction where pinned blocks are desirable.
3. Consider adding one or two more built-in policies only after real workloads justify them.
