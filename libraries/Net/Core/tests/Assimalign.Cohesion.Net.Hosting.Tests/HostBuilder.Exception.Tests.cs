using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Net.Hosting.Tests;

using Assimalign.Cohesion.Net.Hosting.Internal;

public partial class HostBuilderTests
{
    [Fact]
    public async void TestNoServerAddedException()
    {
        Assert.Throws<InvalidHostBuildException>(() => HostBuilder.Create().Build());
    }
}
