# IRezolvrCommandClient

Disposable command protocol contract. `SendCommandAsync(ResourceCommand, CancellationToken)`
and `DeleteCommandAsync(ResourceCommand, CancellationToken)` return a ValueTask of
ResourceCommandObservation. Cancellation and transport failures propagate; server refusals retain
their status and detail. The owner and key are passed through unchanged.
