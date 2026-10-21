using System.Buffers;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

internal static class ProtocolBufferPool
{
    internal static byte[] Rent(int length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        ProtocolEventSource.Log.BufferRented();
        return buffer;
    }

    internal static void Return(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        ProtocolEventSource.Log.BufferReturned();
    }
}
