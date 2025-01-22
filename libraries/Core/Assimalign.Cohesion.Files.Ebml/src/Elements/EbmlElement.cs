using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Assimalign.IO.Ebml;

public class EbmlElement
{
    public static readonly EbmlElement Empty = new EbmlElement();

    public EbmlElement(VInt identifier, long sizeValue, EbmlElementType type)
    {
        Identifier = identifier;
        Size = sizeValue;
        Remaining = sizeValue;
        Type = type;
    }

    private EbmlElement() : this(VInt.UnknownSize(2), 0L, EbmlElementType.None)
    {
    }

    /// <summary>
    /// 
    /// </summary>
    public readonly long Size;
    /// <summary>
    /// 
    /// </summary>
    public readonly VInt Identifier;    

    /// <summary>
    /// 
    /// </summary>
    public bool IsEmpty => !Identifier.IsValidIdentifier && Size == 0 && Type == EbmlElementType.None;

    /// <summary>
    /// 
    /// </summary>
    public bool HasInvalidIdentifier => !Identifier.IsValidIdentifier;

    /// <summary>
    /// 
    /// </summary>
    public long Remaining { get; }

    /// <summary>
    /// 
    /// </summary>
    public EbmlElementType Type { get; }
}
