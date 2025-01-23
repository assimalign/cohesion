using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Ebml;

/// <summary>
/// Thrown to indicate the EBML data format violation.
/// </summary>
public class EbmlDataFormatException : System.IO.IOException
{
    public EbmlDataFormatException()
    {
    }

    public EbmlDataFormatException(string message)
        : base(message)
    {
    }

    public EbmlDataFormatException(string message, Exception cause)
        : base(message, cause)
    {
    }
}
        