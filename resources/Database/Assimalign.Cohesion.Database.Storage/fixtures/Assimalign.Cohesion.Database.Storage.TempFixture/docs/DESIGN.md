# Storage temporary-file crash fixture design

The owning Storage test starts the fixture with an isolated directory, waits for READY, kills
the process tree and waits for exit before reopening the space. A 60-second cancellation guard
bounds a broken fixture; completion depends on process output and exit, never elapsed duration.
Both the actual open run and a seeded closed orphan follow the substrate's owned filename rule.

The parent owns the child lifetime:

```mermaid
sequenceDiagram
    participant Test
    participant Fixture
    participant Space
    Test->>Fixture: Start with isolated directory
    Fixture->>Space: Open run and seed orphan
    Fixture->>Test: READY
    Test->>Fixture: Kill and await exit
    Test->>Space: Reopen and verify sweep
```

The lease-probe mode runs with DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1 in an isolated process.
Create succeeds; CreateRun retries the lease and returns CK05005 on Unix before sweeping.
On Windows the runtime still enforces sharing and the probe succeeds.
