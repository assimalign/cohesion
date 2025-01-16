using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.IO;

public static class StreamExtensions
{

    public static byte[] ReadBytes(this Stream stream, int count, int offset = 0)
    {
        var buffer = new byte[count];
        stream.Read(buffer, offset, count);
        return buffer;
    }

    public static int ReadFully(this Stream stream, byte[] buffer, int offset, int count)
    {
        int bytesRead = 0;
        int totalBytesRead = 0;

        do
        {
            bytesRead = stream.Read(
                buffer, 
                offset + totalBytesRead, 
                count - totalBytesRead);
            totalBytesRead += bytesRead;
        } 
        while (bytesRead > 0 && totalBytesRead < count);

        return totalBytesRead;
    }
}
