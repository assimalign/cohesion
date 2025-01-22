using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public enum ConfigurationSetStrategy
{
    /// <summary>
    /// Only sets configuration values with existing keys
    /// </summary>
    ExistingOnly,
    /// <summary>
    /// Sets the <see cref="IConfigurationEntry"/>
    /// </summary>
    Distributed,
}
