# Database.Tcp

The shared TCP listener integration for the five Database models. See docs/OVERVIEW.md.

Build build/Tasks first in the configuration under test, as for the Database area. Run the
explicit tests/Assimalign.Cohesion.Database.Tcp.Tests.csproj in Debug and Release. The tests bind
real loopback TCP listeners and use thirty-second hang guards. The sample under samples/
Assimalign.Cohesion.Database.Tcp.AotSample publishes with NativeAOT and checks all five models.
