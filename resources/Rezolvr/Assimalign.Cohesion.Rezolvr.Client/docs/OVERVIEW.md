# Assimalign.Cohesion.Rezolvr.Client overview

Create an `IRezolvrCommandClient` with `RezolvrCommandClient.Create` and dispose it when command
delivery finishes. `SendCommandAsync` applies a declaration; `DeleteCommandAsync` removes it.
Both accept a package-local `ResourceCommand` and return a `ResourceCommandObservation`.

The observation preserves `Status` and `Detail`. `Applied` and `Deleted` are successful outcomes;
`Rejected` identifies an HTTP refusal. An empty successful handler response is still successful.
Network failures and cancellation propagate to the caller.

Use a full control-plane URI and an opaque bearer credential issued for the target resource.
The factory validates the URI synchronously; it does not establish a connection. The client does
not acquire tokens, resolve secret sources, or create declarations. Those are gateway responsibilities.

The only dependency is Core for endpoint validation. JSON envelopes use `Utf8JsonWriter` and
observations use `JsonDocument`, with no reflection-based serialization or Hosting dependency.
