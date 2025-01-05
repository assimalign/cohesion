using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Utilities
{
    /// <summary>
    /// Marks the constructor to be used when activating type using <see cref="ActivatorUtilities"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.All)]
    public class ActivatorUtilitiesConstructorAttribute : Attribute
    {
    }
}
