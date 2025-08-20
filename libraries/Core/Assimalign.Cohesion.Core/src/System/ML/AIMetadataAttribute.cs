using System;
using System.Collections.Generic;
using System.Text;

namespace System.ML;

using Assimalign.Cohesion.Internal;

// IDEAS: Use metadata attributes to specify the APIs the AI model should car about. Rather than
// understand the entire API, it can focus on specific attributes that are relevant to its training or operation.
/// <summary>
/// 
/// </summary>
internal class AIMetadataAttribute : Attribute
{
    public AIMetadataAttribute(string meta)
    {
        Meta = ThrowHelper.ThrowIfNullOrEmpty(meta);
    }

    public string Meta { get; }
}
