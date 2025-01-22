using System;
using System.IO;


namespace Assimalign.Cohesion.Files.Bmff;

using Assimalign.Cohesion.Files.Bmff.Internal;

/// <summary>
/// 
/// </summary>
public abstract class BmffReader : IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    public abstract BmffBox Current { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract bool Read();

    /// <inheritdoc />
    public abstract void Dispose();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="stream"></param>
    /// <returns></returns>
    public static BmffReader Create(Stream stream)
    {
        return new BmffReaderDefault(new BmffStream(stream, 0, stream.Length));
    } 
}
