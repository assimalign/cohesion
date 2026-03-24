using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara.ApplicationModel;


[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ResourceAttribute<TResource> : Attribute 
    where TResource : ISyntharaResource, new()
{
    public ResourceAttribute()
    {
        Resource = new TResource();
    }

    /// <summary>
    /// 
    /// </summary>
    public TResource Resource { get; }
}
