using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A database-bound document or collection operation. Unused fields must be empty.</summary>
/// <param name="Operation">The operation discriminator.</param>
/// <param name="Collection">The collection name; empty only for ListCollections.</param>
/// <param name="Id">The document identity for Get, Put and Delete; empty otherwise.</param>
/// <param name="ExpectedVersion">The optional version precondition for Put and Delete.</param>
/// <param name="Content">The UTF-8 JSON bytes for Put; empty otherwise.</param>
public sealed record DocumentProtocolCommandMessage(DocumentProtocolOperation Operation, string Collection = "",
    string Id = "", DocumentVersion? ExpectedVersion = null, ReadOnlyMemory<byte> Content = default)
{
    /// <summary>Encodes the operation-specific payload.</summary>
    /// <returns>The encoded payload.</returns>
    /// <exception cref="ProtocolException">The operation or its fields are invalid.</exception>
    public byte[] Encode()
    {
        Validate();
        var buffer = new List<byte> { (byte)Operation };
        if (Operation != DocumentProtocolOperation.ListCollections) { ProtocolPayload.WriteString(buffer, Collection); }
        if (Operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete)
        {
            ProtocolPayload.WriteString(buffer, Id);
        }
        if (Operation is DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete)
        {
            buffer.Add(ExpectedVersion.HasValue ? (byte)1 : (byte)0);
            if (ExpectedVersion is { } version) { ProtocolPayload.WriteInt64(buffer, unchecked((long)version.Value)); }
        }
        if (Operation == DocumentProtocolOperation.Put)
        {
            ProtocolPayload.WriteInt32(buffer, Content.Length);
            buffer.AddRange(Content.ToArray());
        }
        return buffer.ToArray();
    }

    /// <summary>Decodes and validates an operation-specific payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The decoded command with owned content.</returns>
    /// <exception cref="ProtocolException">The payload is invalid, truncated or has trailing bytes.</exception>
    public static DocumentProtocolCommandMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) { throw new ProtocolException(ErrorMessages.OperationMissing); }
        int position = 1;
        var operation = (DocumentProtocolOperation)payload[0];
        string collection = operation == DocumentProtocolOperation.ListCollections ? "" : ProtocolPayload.ReadString(payload, ref position);
        string id = operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete
            ? ProtocolPayload.ReadString(payload, ref position) : "";
        DocumentVersion? version = null;
        if (operation is DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete)
        {
            if (position >= payload.Length || payload[position] > 1) { throw new ProtocolException(ErrorMessages.VersionMarkerInvalid); }
            if (payload[position++] == 1) { version = new(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position))); }
        }
        ReadOnlyMemory<byte> content = default;
        if (operation == DocumentProtocolOperation.Put)
        {
            int length = ProtocolPayload.ReadInt32(payload, ref position);
            if (length < 0 || length != payload.Length - position) { throw new ProtocolException(ErrorMessages.ContentLengthInvalid); }
            content = payload[position..].ToArray();
            position += length;
        }
        DocumentProtocolWire.EnsureEnd(payload, position);
        var command = new DocumentProtocolCommandMessage(operation, collection, id, version, content);
        command.Validate();
        return command;
    }

    private void Validate()
    {
        bool document = Operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete;
        bool write = Operation is DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete;
        if (!Enum.IsDefined(Operation) || Collection is null || Id is null ||
            (Operation == DocumentProtocolOperation.ListCollections ? Collection.Length != 0 : string.IsNullOrWhiteSpace(Collection)) ||
            (document ? string.IsNullOrWhiteSpace(Id) : Id.Length != 0) ||
            (!write && ExpectedVersion.HasValue) || (Operation != DocumentProtocolOperation.Put && !Content.IsEmpty))
        {
            throw new ProtocolException(ErrorMessages.OperationFieldsInvalid);
        }
        if (Operation == DocumentProtocolOperation.Put) { DocumentProtocolJson.Validate(Content.Span); }
    }
}
