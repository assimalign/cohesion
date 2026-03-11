using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests
{
    public class ConfigurationSectionTest
    {

        [Fact]
        public void TestSectionExpand()
        {
            var configuration = new ConfigurationBuilder()
                .AddProvider(context => new TempProvider())
                .Build();





            //section["Azure:Identity:ClientSecret"] = "asdflkajdsf";
            //section["Azure:Identity:ClientId"] = Guid.NewGuid().ToString();
            //section["Azure:Identity:Endpoint"] = "https://auth.com/config";


            ////var secret = configuration["Azure:Identity:ClientSecret"];
            ////var section = configuration.GetSection("Azure:Identity");





        }


        partial class TempProvider : ConfigurationProvider
        {
            public TempProvider()
            {
                
            }

            public override string Name => throw new NotImplementedException();
            protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
            {
                entries["Azure:Identity:ClientSecret"] = "asdflkajdsf";
                entries["Azure:Identity:ClientId"] = Guid.NewGuid().ToString();
                entries["Azure:Identity:Endpoint"] = "https://auth.com/config";

                return Task.CompletedTask;
            }
        }
    }
}
