using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationBuilderTests
{
    public ConfigurationBuilderTests()
    {
        var builder = ConfigurationBuilder.Create(options =>
        {
            
        });

        builder.AddProvider(context =>
        {
            
        });

        //builder.AddProvider(context =>
        //{
        //    context.
        //});
    }
}
