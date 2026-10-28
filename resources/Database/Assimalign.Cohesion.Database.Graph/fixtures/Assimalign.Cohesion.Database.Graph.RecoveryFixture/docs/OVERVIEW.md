# Graph recovery fixture

This non-packable executable is launched by `GraphProcessTests`. `seed directory` creates and
commits a graph path and property index, writes an uncommitted detach and replacement path, then
exits without disposal. `verify directory` reopens through the engine and checks committed data,
adjacency and catalog recovery. The test runs verification twice.

`GraphOrdinaryWriteAllocationTests` also launches `allocations injectedKind [injectedBytes]`.
The isolated process warms CREATE, SET and DELETE on one thread, then reports their minimum
allocated bytes per statement. `injectedKind` is -1 for no injection or 0/1/2 for CREATE/SET/DELETE;
the optional payload defaults to 1024 bytes and lets the guards prove their allocation headroom.
