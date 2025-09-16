using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

internal interface IConfigurationSource
{

    /// <summary>
    /// Returns a <see cref="IChangeToken"/> that can be used to listen for changes
    /// </summary>
    /// <returns></returns>
    IChangeToken GetChangeToken();
}
