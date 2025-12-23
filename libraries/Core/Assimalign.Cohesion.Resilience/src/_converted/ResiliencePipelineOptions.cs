using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public sealed class ResiliencePipelineOptions
{

    /// <summary>
    /// 
    /// </summary>
    public required ResilienceStrategy Strategy { get; set; }

    /// <summary>
    /// The underlying context creation factory to use wehn executing pipeline. (Optional)
    /// </summary>
    public Func<ResilienceContextCreationArguments, ResilienceContext>? ContextFactory { get; set; }

}
