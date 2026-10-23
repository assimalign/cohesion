# Database.Protocol overview

The shared mechanism for every Cohesion database model: bounded framing, handshake,
session lifecycle, errors, version negotiation, and immutable endpoint family binding.
Model packages own their message identifiers and payload codecs.

- ProtocolFrameHeader / ProtocolFrame: five-byte envelope and payload.
- ProtocolMessageType: core handshake, error, negotiated Diagnostics, liveness, and termination identifiers.
- ProtocolMessageFamily / ProtocolChannel: immutable endpoint family and validated I/O.
- ProtocolFrameReader / ProtocolFrameWriter: the abstract frame reader and writer; their
  `Create` factories return the raw stream reader and writer for mechanism and diagnostics.
- ProtocolPayload: big-endian integers and length-prefixed UTF-8 string primitives.
- ProtocolVersion: version 2.1 startup and capability negotiation, rejecting incompatible majors before authentication.
- ProtocolErrorCode / ProtocolException: shared error taxonomy and framing failures.

This child root has no dependencies on models, the area root, or transports. The
area root references it. See [design and wire contract](DESIGN.md).

SQL and Graph negotiate transaction-status: each Execute ends with a Ready I/T/E status, enabling rollback before pool reuse. Existing identifiers and protocol 2.1 are retained (#1540).

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
