using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>A bounded list of structured statement diagnostics.</summary>
/// <param name="Entries">The diagnostics in source order.</param>
/// <exception cref="ArgumentNullException">The collection is null, including an init assignment.</exception>
public sealed record ProtocolDiagnosticsMessage(IReadOnlyList<ProtocolDiagnostic> Entries)
{
    private IReadOnlyList<ProtocolDiagnostic> _entries = Entries ?? throw new ArgumentNullException(nameof(Entries));

    /// <summary>Gets or initializes the non-null collection.</summary>
    /// <exception cref="ArgumentNullException">The assigned collection is null.</exception>
    public IReadOnlyList<ProtocolDiagnostic> Entries
    {
        get => _entries;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _entries = value;
        }
    }

    /// <summary>The maximum diagnostics per statement message.</summary>
    public const int MaxEntryCount = 4096;
    /// <summary>The maximum encoded payload size, in bytes.</summary>
    public const int MaxPayloadLength = 1_048_576;

    /// <summary>Encodes diagnostics without reflection.</summary>
    /// <returns>The frame payload.</returns>
    /// <exception cref="ProtocolException">An entry is invalid or a bound is exceeded.</exception>
    public byte[] Encode()
    {
        if (Entries.Count > MaxEntryCount) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
        var buffer = new List<byte>();
        ProtocolPayload.WriteInt32(buffer, Entries.Count);
        foreach (var entry in Entries)
        {
            Validate(entry);
            long size = 1L + 8 + Encoding.UTF8.GetByteCount(entry.Code ?? string.Empty) + Encoding.UTF8.GetByteCount(entry.Message ?? string.Empty) + 12;
            if (size > MaxPayloadLength - buffer.Count) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
            buffer.Add((byte)entry.Severity);
            ProtocolPayload.WriteString(buffer, entry.Code ?? string.Empty);
            ProtocolPayload.WriteString(buffer, entry.Message ?? string.Empty);
            ProtocolPayload.WriteInt32(buffer, entry.Start ?? -1);
            ProtocolPayload.WriteInt32(buffer, entry.End ?? -1);
            ProtocolPayload.WriteInt32(buffer, entry.Line);
        }
        return buffer.ToArray();
    }

    /// <summary>Decodes a bounded diagnostics payload using checked span reads.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded diagnostics.</returns>
    /// <exception cref="ProtocolException">The payload is malformed or exceeds a bound.</exception>
    public static ProtocolDiagnosticsMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
        int position = 0;
        int count = ProtocolPayload.ReadInt32(payload, ref position);
        if (count < 0 || count > MaxEntryCount || count > (payload.Length - position) / 21) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
        var entries = new ProtocolDiagnostic[count];
        for (int index = 0; index < count; index++)
        {
            if (position == payload.Length) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
            var severity = (ProtocolDiagnosticSeverity)payload[position++];
            string code = ProtocolPayload.ReadString(payload, ref position);
            string message = ProtocolPayload.ReadString(payload, ref position);
            int start = ProtocolPayload.ReadInt32(payload, ref position);
            int end = ProtocolPayload.ReadInt32(payload, ref position);
            int line = ProtocolPayload.ReadInt32(payload, ref position);
            if (start < -1 || end < -1) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
            var entry = new ProtocolDiagnostic(severity, code.Length == 0 ? null : code, message.Length == 0 ? null : message, start == -1 ? null : start, end == -1 ? null : end, line);
            Validate(entry);
            entries[index] = entry;
        }
        if (position != payload.Length) { throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid); }
        return new(Array.AsReadOnly(entries));
    }

    private static void Validate(ProtocolDiagnostic entry)
    {
        if (entry is null || entry.Severity is not (ProtocolDiagnosticSeverity.Warning or ProtocolDiagnosticSeverity.Error) ||
            entry.Start < 0 || entry.End < 0 ||
            entry.Start.HasValue != entry.End.HasValue || entry.End < entry.Start || entry.Line < 1)
        {
            throw new ProtocolException(ErrorMessages.DiagnosticPayloadInvalid);
        }
    }
}
