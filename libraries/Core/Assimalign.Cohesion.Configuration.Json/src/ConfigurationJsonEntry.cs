using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public abstract class ConfigurationJsonEntry : IConfigurationEntry
{
    public abstract Key Key { get; }
}
