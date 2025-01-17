using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class TestUser
{
    public string Username { get; set; }
    public int Age { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string MiddleName { get; set; }
    public DateTime? Birthdate { get; set; }
    public IList<TestPersonAddress> Addresses { get; set; }
}
