using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;


/// <summary>
/// A BMFF is arranged in a composite design pattern
/// </summary>
public abstract class BmffBox
{
    /// <summary>
    /// 
    /// </summary>
    public abstract bool IsLeaf { get; }
    /// <summary>
    /// 
    /// </summary>
    public abstract bool IsComposite { get; }
    /// <summary>
    /// The size of the box.
    /// </summary>
    public abstract long Limit { get; }
    /// <summary>
    /// Represents the starting position in a stream for the specific box.
    /// </summary>
    /// <remarks>
    /// <b>NOTE:</b> This is usually the position of the stream in which the box was parsed.
    /// </remarks>
    public abstract long Offset { get; }
    /// <summary>
    /// Represents the known BMFF box type.
    /// </summary>
    public abstract BmffBoxType BoxType { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="stream"></param>
    public abstract void Read(BmffStream stream);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="stream"></param>
    public abstract void Write(BmffStream stream);
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="visitor"></param>
    /// <returns></returns>
    public virtual T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}