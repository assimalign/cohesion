using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Net.Http.Tests;

public partial class ValueTypeTests
{
    [Fact(DisplayName = "Value Type (HttpQueryKey) - Test Null Or Empty String Exception")]
    public void TestNullOrEmptyStringException()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            HttpQueryKey key = "";
        });

        Assert.Throws<ArgumentNullException>(() =>
        {
            var key = new HttpQueryKey(default);
        });
    }

    [Fact(DisplayName = "Value Type (HttpQueryKey) - Test Duplicate key ArgumentException")]
    public void TestDuplicateKeyArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var dictionary = new Dictionary<HttpQueryKey, string>()
            {
                {"test", "test" }
            };

            dictionary.Add("test", "test");
        });
    }

    [Fact(DisplayName = "Value Type (HttpQueryKey) - Sorting/HashSet Test")]
    public void TestSortingAndHashSet()
    {
        var keys = new HashSet<HttpQueryKey>()
        {
            "chase",
            "Chase",
            "Daniel",
            "Jim",
            "ChArles"
        }.OrderBy(p => p).ToHashSet();

        Assert.Equal(4, keys.Count);
        Assert.Equal<HttpQueryKey>("charles", keys.First());
    }
}