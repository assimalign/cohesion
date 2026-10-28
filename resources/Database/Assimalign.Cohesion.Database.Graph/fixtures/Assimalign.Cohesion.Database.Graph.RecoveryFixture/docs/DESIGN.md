# Graph recovery fixture design

The fixture depends only on the Graph engine's public composition and session surfaces. A separate
process is required so ending the process bypasses automatic transaction disposal and rollback.
Manual page-writeback exercises recovery of physical pages containing uncommitted logical writes;
the WAL commit record determines which effects remain after reopening.

The `allocations injectedKind [injectedBytes]` mode runs a separate, fully disposed in-memory
engine with fixed label identities and hour-long worker intervals. It warms 100 write rounds,
then takes each CREATE/SET/DELETE minimum over five 100-round samples using the current thread's
allocation counter. Every operation must complete synchronously and affect one entity. The
optional injected payload is allocated inside that measured bracket, testing both 1 KiB
regressions and the 128-byte SET/DELETE headroom. Recovery's `seed directory` and `verify directory`
modes retain their existing process-crash behavior.

The diagram shows the fixture and test dependencies described above.

```mermaid
flowchart LR
    Test["GraphProcessTests"] --> Fixture["Recovery fixture executable"]
    Fixture --> Engine["GraphDatabaseEngine"]
```
