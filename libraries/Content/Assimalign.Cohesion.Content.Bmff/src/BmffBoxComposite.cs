using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

/// <summary>
/// 
/// </summary>
public abstract class BmffBoxComposite : BmffBox
{
    public BmffBoxComposite()
    {
        
    }
    /// <inheritdoc />
    public override bool IsLeaf => false;

    /// <inheritdoc />
    public override bool IsComposite => true;
    /// <summary>
    /// 
    /// </summary>
    public abstract IEnumerable<BmffBox> Children { get; }
}
