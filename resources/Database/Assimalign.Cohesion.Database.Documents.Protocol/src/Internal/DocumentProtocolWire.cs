using System;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Protocol.Internal;

internal static class DocumentProtocolWire
{
    internal static void EnsureEnd(ReadOnlySpan<byte> payload, int position)
    {
        if (position != payload.Length) { throw new ProtocolException(ErrorMessages.PayloadTrailingBytes); }
    }
}
