using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMethodTests
{
    [Theory]
    [InlineData("get", "GET")]
    [InlineData("pOSt", "POST")]
    [InlineData("PUT", "PUT")]
    [InlineData("COnnECT", "CONNECT")]
    public void MethodParseTest(string value, string expected)
    {
        HttpMethod method = value;

        Assert.Equal(method.Value, expected);
    }
}
