using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

using Configuration;

internal static partial class ThrowHelper
{

    internal static ConfigurationException GetConfigurationException(string message) =>
        new ConfigurationException(message);
   
}