# Blob protocol API

The Blob engine package also owns its model-specific wire codecs. The exact version 1 payload
layout and request lifecycle are defined in [DESIGN.md](../../DESIGN.md#blob-wire-family).

| Type | Purpose |
| --- | --- |
| `BlobProtocol` | Immutable `Family` and the 65,536-byte `MaxChunkLength` constant |
| `BlobProtocolMessageType` | Endpoint-scoped message identifiers 64–69 |
| `BlobReadMessage` | Container and object identity for a download |
| `BlobWriteMessage` | Container, object identity, and overwrite flag for an upload |
| `BlobTransferStartMessage` | Expected length (`-1` means unknown) and content type |
| `BlobChunkMessage` | A caller-owned memory view; `ToFrame` and `Decode` validate the chunk size without copying |
| `BlobChunkAcknowledgementMessage` | Cumulative content count accepted by the receiver |
| `BlobTransferCompleteMessage` | Actual content count at transfer completion or upload publication |
| `BlobProtocolTransfer` | Sequential `SendAsync` and `ReceiveAsync` over a channel bound to `BlobProtocol.Family` |

Metadata messages expose `Encode()` and static `Decode(ReadOnlySpan<byte>)`. Encoding or decoding
invalid values throws `ProtocolException`. Names are nonempty; strings use strict UTF-8 and each
is limited to 65,535 encoded bytes. Completion and acknowledgement counts are nonnegative.
`BlobChunkMessage.Decode` takes `ReadOnlyMemory<byte>` and retains that memory; the source memory
must remain valid until the receiver finishes consuming it or the awaited frame write completes.

The transport and content streams remain owned by the caller. After the shared handshake and
the appropriate Read or Write request, a sending side can use:

```csharp
long transferred = await BlobProtocolTransfer.SendAsync(
    channel, source, new BlobTransferStartMessage(-1, "application/octet-stream"), cancellationToken);
```

The receiving side concurrently calls:

```csharp
BlobTransferStartMessage content = await BlobProtocolTransfer.ReceiveAsync(
    channel, destination, cancellationToken);
// content.Length is the verified actual count, including for an initially unknown length.
```

The helpers support non-seekable streams and await one acknowledgement per chunk, bounding
content in flight. They do not open or pool connections, select databases, authenticate, dispatch
requests, flush or dispose destination storage, or commit an upload. A server must publish a
verified upload before sending the final `TransferComplete` acknowledgement to its client.

Only one operation may use a channel at a time. Wrong-family channels or unsuitable streams
throw `ArgumentException`; null arguments throw `ArgumentNullException`; malformed, premature,
or error-terminated exchanges throw `ProtocolException`; cancellation propagates as
`OperationCanceledException`. Source and destination I/O exceptions also propagate. After a
failed operation, close the connection and abort destination storage rather than attempting
to continue from a partial transfer.
