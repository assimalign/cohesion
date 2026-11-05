# Storage temporary-file crash fixture

A non-packable executable launched by StorageTempSpaceTests. It opens a real space and run,
prints READY, then waits for its parent to kill it. A separately seeded owned orphan verifies
startup sweeping on platforms where delete-on-close already removes every live run.

The lease-probe mode seeds an owned orphan, opens the space, then attempts CreateRun. It prints
LEASE_REFUSED with the cause when Unix locking is disabled, preserving the orphan; Windows
prints LEASE_OK. Startup itself remains available.
