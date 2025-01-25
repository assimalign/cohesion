using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class BindingTests
{

    public class TestOptions
    {

        [ConfigurationBinding<List<Feature>>]
        public IEnumerable<Feature>  Features { get; set; }
    }


    public class Feature
    {

    }
}
