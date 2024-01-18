using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Net.Http.Tests;

public class HttpQueryCollectionTests
{
    
    public void Test()
    {
        var collection = new HttpQueryCollection();

        foreach (var query in collection)
        {
            //int value = query;
        }
    }
}
