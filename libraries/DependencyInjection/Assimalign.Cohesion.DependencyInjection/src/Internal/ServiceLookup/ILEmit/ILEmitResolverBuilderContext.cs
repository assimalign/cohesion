using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class ILEmitResolverBuilderContext
{
    public ILGenerator Generator { get; set; }
    public List<object> Constants { get; set; }
    public List<Func<IServiceProvider, object>> Factories { get; set; }
}
