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

SQL, Graph and Documents negotiate transaction-status: Execute, Graph ExecutePaths and Documents
Command end with Ready I/T/E status, enabling rollback before pool reuse. Reset retains each
family's existing envelope: SQL/Graph use 5/8 and Documents uses 64/66 with two completion counts.
Existing identifiers and protocol 2.1 are retained (#1540, #1725).

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.

## Debugger inspection convention

Protocol debugger views filter private framing/session machinery and project only reviewed public/protected names, versions, capability flags, already-materialized collections and relationships. They do not perform stream I/O, decode payloads, negotiate capabilities or materialize a new collection. Error/Exception subclasses and EventSource types remain excluded.

Concrete eligible types carry their own concise display and private nested DebugView at the end of the type; an effective inherited display/proxy pair is reused. Projections are public read-only members of that private view and never reveal private/internal data. Empty views use the constant type name. Debugger attributes and nested infrastructure add no public API, instance fields to existing runtime types, storage/wire format change or successful-path work.
