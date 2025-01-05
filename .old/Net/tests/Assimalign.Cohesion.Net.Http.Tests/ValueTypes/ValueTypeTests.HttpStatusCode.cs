using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Tests;

public partial class ValueTypeTests
{
    [Fact(DisplayName = "Value Type (HttpStatusCode) - Invalid Status Code Exception")]
    public void TestInvalidStatusException()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            HttpStatusCode statusCode = 600;
        });
    }
}
