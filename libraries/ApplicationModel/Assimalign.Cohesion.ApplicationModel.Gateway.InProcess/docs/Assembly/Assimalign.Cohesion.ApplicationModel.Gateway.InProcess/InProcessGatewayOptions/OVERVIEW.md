# InProcessGatewayOptions

`InProcessGatewayOptions` extends `ApplicationGatewayOptions` with settings specific to nested
resource hosts.

- `StateDirectory` roots persisted loopback ports and private claims.
- `ProbeInterval` and `ProbeTimeout` bound health observation.
- `LivenessFailureThreshold` defaults to three consecutive failures.
- `InitialRestartBackoff`, `MaximumRestartBackoff`, and `MaximumRestartAttempts` bound retries.

Invalid empty paths, non-positive probe durations or thresholds, negative retry values, and a
maximum backoff below the initial backoff are rejected by the `InProcessGateway` constructor.
