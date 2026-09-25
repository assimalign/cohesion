# Graph recovery fixture

This non-packable executable is launched by `GraphProcessTests`. `seed directory` creates and
commits a graph path and property index, writes an uncommitted detach and replacement path, then
exits without disposal. `verify directory` reopens through the engine and checks committed data,
adjacency and catalog recovery. The test runs verification twice.
