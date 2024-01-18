using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Storage;

public static class StreamExtensions
{


    public static Task ReadAsync(this Stream stream)
    {
        if (stream is FileStream file)
        {
            RandomAccess.ReadAsync(file.Handle, )
        }
        else
        {
            stream.ReadAsync()
        }

        return Task.CompletedTask;
    }
}
