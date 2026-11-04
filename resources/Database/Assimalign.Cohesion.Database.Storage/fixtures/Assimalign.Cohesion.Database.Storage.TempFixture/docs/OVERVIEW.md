# Storage temporary-file crash fixture

A non-packable executable launched by StorageTempSpaceTests. It opens a real space and run,
prints READY, then waits for its parent to kill it. A separately seeded owned orphan verifies
startup sweeping on platforms where delete-on-close already removes every live run.
