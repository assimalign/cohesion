using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public class ConfigurationException : CohesionException
{
    public ConfigurationException(string message) : base(message)
    {
    }
    public ConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
