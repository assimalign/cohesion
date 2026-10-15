# InProcessGatewayOptions

`InProcessGatewayOptions` extends `ApplicationGatewayOptions` with settings specific to nested
resource hosts.

- `StateDirectory` roots persisted loopback ports and private claims.
- `ProbeInterval` and `ProbeTimeout` bound health observation.
- `LivenessFailureThreshold` defaults to three consecutive failures.
- `InitialRestartBackoff`, `MaximumRestartBackoff`, and `MaximumRestartAttempts` bound retries.

`LivenessFailureThreshold` must be greater than zero, and `MaximumRestartAttempts` must be
nonnegative; zero disables restarts. Invalid values throw `ArgumentOutOfRangeException` when
assigned, including in an object initializer and after gateway construction.

The `InProcessGateway` constructor validates the remaining options, rejecting empty paths,
non-positive probe durations, a negative initial restart backoff, and a maximum restart backoff
below the initial restart backoff.
