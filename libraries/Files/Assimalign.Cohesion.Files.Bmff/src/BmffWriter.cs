using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;


public delegate Task BmffWriterCallback(Stream stream);

public abstract class BmffWriter : IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="box"></param>
    public abstract void Write(BmffBox box);
    /// <summary>
    /// Write unsupported boxes to the stream.
    /// </summary>
    /// <param name="callback"></param>
    public abstract void Write(BmffWriterCallback callback);

    public abstract void Dispose();
}
