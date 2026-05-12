# Transport Review 01

## Finding

Public buffer and scheduling options are inconsistent across transports.

- UDP options expose TCP-like tuning knobs that are not currently honored.
- QUIC server options expose max buffer settings that are not currently honored.
- This makes the API harder to trust because configuration appears richer than runtime behavior.

## Solution Path A

Honor the existing public options wherever they are exposed.

### UDP

- Add internal `PipeOptions` factories on UDP client/server options.
- Use `PipeMemoryPool`, `PipeScheduler`, and read/write thresholds in `UdpTransportConnection`.
- Keep the existing public option names.
- Leave `IOQueueCount` and `WaitForDataBeforeAllocatingBuffer` either partially implemented or documented as future-facing if full parity is not practical yet.

### QUIC

- Extend `TransportConnectionPipe(Stream)` so it can accept `StreamPipeReaderOptions` and `StreamPipeWriterOptions`.
- Map QUIC buffer settings to explicit stream reader/writer buffering configuration.
- Keep the existing public option names, but tighten docs so they describe stream buffering instead of pipe backpressure if needed.

### Benefits

- Preserves the current public API shape.
- Keeps transport setup familiar across protocols.
- Minimizes churn for callers already using these properties.

### Drawbacks

- QUIC does not naturally map to the same semantics as TCP pipe thresholds.
- The option model stays broad and slightly misleading unless every knob is implemented fully.
- Higher maintenance cost because each transport must emulate a common surface differently.

## Solution Path B

Make the public configuration surface honest, even if it means a breaking API change.

### UDP

- Remove unsupported TCP-like knobs from UDP options.
- Replace them with explicit `PipeOptions`-style or transport-specific configuration.
- Keep only options that the UDP transport actually uses.

### QUIC

- Remove `MaxReadBufferSize` and `MaxWriteBufferSize` from QUIC server options.
- Replace them with explicit stream pipe reader/writer options only if needed.

### Benefits

- Much clearer mental model.
- Lower long-term maintenance cost.
- Less risk that callers think they are tuning something that is ignored.

### Drawbacks

- Breaking change.
- Less visual consistency between transport option types.
- Requires callers to learn more transport-specific configuration.

## Recommended Direction

Prefer honest configuration over fake parity.

Recommended implementation sequence:

1. UDP: keep and honor read/write pipe sizing where it is meaningful.
2. UDP: remove or postpone knobs that imply IO queueing behavior that the current UDP implementation does not have.
3. QUIC: replace max buffer properties with explicit stream pipe reader/writer configuration, or remove them until they can be implemented faithfully.

This keeps the API trustworthy without forcing QUIC and UDP to pretend they share TCP's exact buffering model.
