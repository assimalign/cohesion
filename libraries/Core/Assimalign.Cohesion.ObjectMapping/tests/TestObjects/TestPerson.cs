using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class TestPerson
{
    public string Username { get; set; }
    public TestPersonDetails Details { get; set; }

    public IEnumerable<TestPersonAddress> Addresses { get; set; }
}
