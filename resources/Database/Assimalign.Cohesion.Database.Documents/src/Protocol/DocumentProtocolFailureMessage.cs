using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A complete rejected statement response, after which the session is ready again.</summary>
/// <param name="Code">ParseFailure, ExecutionFailure or Unavailable.</param>
/// <param name="Message">The server's failure description.</param>
/// <param name="Diagnostics">The server's structured diagnostics, empty for an uncoded engine failure.</param>
public sealed record DocumentProtocolFailureMessage(ProtocolErrorCode Code, string Message, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Encodes the failure and all diagnostic locations.</summary>
    /// <returns>The terminal failure payload.</returns>
    /// <exception cref="ProtocolException">The code or diagnostics are invalid.</exception>
    public byte[] Encode()
    {
        ValidateCode(Code);
        var buffer = new List<byte>();
        ProtocolPayload.WriteUInt16(buffer, (ushort)Code);
        ProtocolPayload.WriteString(buffer, Message);
        ProtocolPayload.WriteInt32(buffer, Diagnostics.Count);
        foreach (var diagnostic in Diagnostics)
        {
            ProtocolPayload.WriteString(buffer, diagnostic.Code ?? "");
            ProtocolPayload.WriteString(buffer, diagnostic.Message ?? "");
            ProtocolPayload.WriteInt32(buffer, diagnostic.Start ?? -1);
            ProtocolPayload.WriteInt32(buffer, diagnostic.End ?? -1);
            ProtocolPayload.WriteInt32(buffer, diagnostic.Line);
            ProtocolPayload.WriteInt32(buffer, diagnostic.Severity is { } severity ? (int)severity : -1);
            ProtocolPayload.WriteInt32(buffer, diagnostic.Location is { } location ? (int)location : -1);
        }
        // Validate the same constraints as the peer before putting bytes on the wire.
        byte[] payload = buffer.ToArray();
        _ = Decode(payload);
        return payload;
    }

    /// <summary>Decodes a terminal failure without interpreting its message text.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The failure and owned diagnostics.</returns>
    /// <exception cref="ProtocolException">The payload is invalid or has trailing bytes.</exception>
    public static DocumentProtocolFailureMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        var code = (ProtocolErrorCode)ProtocolPayload.ReadUInt16(payload, ref position);
        ValidateCode(code);
        string message = ProtocolPayload.ReadString(payload, ref position);
        int count = ProtocolPayload.ReadInt32(payload, ref position);
        if (count < 0 || count > (payload.Length - position) / 28) { throw new ProtocolException(ErrorMessages.DiagnosticCountInvalid); }
        var diagnostics = new Diagnostic[count];
        for (int index = 0; index < count; index++)
        {
            string diagnosticCode = ProtocolPayload.ReadString(payload, ref position);
            string detail = ProtocolPayload.ReadString(payload, ref position);
            int start = ProtocolPayload.ReadInt32(payload, ref position);
            int end = ProtocolPayload.ReadInt32(payload, ref position);
            int line = ProtocolPayload.ReadInt32(payload, ref position);
            int severity = ProtocolPayload.ReadInt32(payload, ref position);
            int location = ProtocolPayload.ReadInt32(payload, ref position);
            if (start < -1 || end < -1 || (start >= 0 && end >= 0 && end < start) || line < 1 ||
                (severity != -1 && !Enum.IsDefined((DiagnosticSeverity)severity)) ||
                (location != -1 && !Enum.IsDefined((DiagnosticLocation)location)))
            {
                throw new ProtocolException(ErrorMessages.DiagnosticFieldsInvalid);
            }
            diagnostics[index] = new Diagnostic
            {
                Code = diagnosticCode.Length == 0 ? null : diagnosticCode,
                Message = detail.Length == 0 ? null : detail,
                Start = start == -1 ? null : start, End = end == -1 ? null : end, Line = line,
                Severity = severity == -1 ? null : (DiagnosticSeverity)severity,
                Location = location == -1 ? null : (DiagnosticLocation)location,
            };
        }
        DocumentProtocolWire.EnsureEnd(payload, position);
        return new(code, message, Array.AsReadOnly(diagnostics));
    }

    private static void ValidateCode(ProtocolErrorCode code)
    {
        if (code is not (ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure or ProtocolErrorCode.Unavailable))
        {
            throw new ProtocolException(ErrorMessages.FailureCodeInvalid);
        }
    }
}
