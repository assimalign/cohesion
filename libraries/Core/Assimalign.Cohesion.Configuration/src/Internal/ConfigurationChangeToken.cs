using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Internal
{
    internal class ConfigurationChangeToken : IConfigurationChangeToken
    {
        private IEnumerable<IConfigurationChangeToken> tokens;

        public ConfigurationChangeToken(IEnumerable<IConfigurationChangeToken> tokens)
        {
            
        }




        public IDisposable OnAdd(Action<IConfiguration> action)
        {
            
            throw new NotImplementedException();
        }

        public IDisposable OnChange(Action<IConfiguration> callback)
        {
            throw new NotImplementedException();
        }

        public IDisposable OnChange(Action<object> callback)
        {
            throw new NotImplementedException();
        }
    }
}
