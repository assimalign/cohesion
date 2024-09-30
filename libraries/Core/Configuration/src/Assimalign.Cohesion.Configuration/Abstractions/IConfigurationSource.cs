using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public interface IConfigurationSource
{
    /// <summary>
    /// Builds the <see cref="IConfigurationProvider"/> for this source.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    /// <returns>An <see cref="IConfigurationProvider"/></returns>
    IConfigurationProvider Build(IConfigurationBuilder builder);
}
