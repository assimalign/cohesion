using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests
{
    public class ConfigurationSectionTest
    {

        [Fact]
        public void TestSectionExpand()
        {
            var configuration = new ConfigurationBuilder()
                .AddProvider(context =>
                {
                    return MockConfigurationProvider.Create(new Dictionary<KeyPath, object>()
                    {
                        { "IsEnabled", true },
                        { "Azure:Identity:ClientId", new Guid("afdc6951-fac8-4e68-8b02-a8acdda7558e") }
                    });
                })
                .Build();


            configuration["Azure:Identity:ClientSecret"] = "asdflkajdsf";
            configuration["Azure:Identity:ClientSecret"] = Guid.NewGuid();

            var secret = configuration["Azure:Identity:ClientSecret"];
            var section = configuration.GetSection("Azure:Identity");





        }
    }
}
