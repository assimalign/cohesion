using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class KeyTests
{

    [Fact]
    public void Test()
    {
        var key1 = default(Key);
        var key2 = "test";

        var matches = key1.Equals(key2);

    }

    //[Theory(DisplayName = "Configuration Test: Key Value Null Exception")]
    //[InlineData("")]
    //[InlineData(null)]
    //public void KeySegmentNullException(string? value)
    //{
    //    Assert.Throws<ArgumentNullException>(() =>
    //    {
    //        var segment = new KeySegment(value!);
    //    });
    //}

    //[Theory]
    //[InlineData("key1", true)]
    //[InlineData("key1[34]", true)]
    //[InlineData("key1$label1", true)]
    //[InlineData("key1$la:bel1", false)]     // Cannot contain separator
    //[InlineData("key1[34]$label1", false)]  // Label MUST come before indexer
    //[InlineData("key1$label1[56]", true)]
    //public void KeySegmentTestsParsing(string value, bool isValid)
    //{
    //    if (isValid)
    //    {
    //        var segment = new KeySegment(value);

    //        Assert.Equal("key1", segment.Value);

    //        if (segment.HasLabel(out var label))
    //        {
    //            Assert.Contains(label!, value);
    //        }
    //        if (segment.HasIndex(out var index))
    //        {
    //            Assert.Contains(index.ToString()!, value);
    //        }
    //    }
    //    else
    //    {
    //        Assert.Throws<ArgumentException>(() =>
    //        {
    //            var segment = new KeySegment(value);
    //        });
    //    }
    //}


    //[Theory]
    //[InlineData("key1", "key1", KeyComparison.Ordinal, true)]
    //[InlineData("key1", "key2", KeyComparison.Ordinal, false)]
    //[InlineData("key1", "Key1", KeyComparison.Ordinal, false)]
    //[InlineData("key1", "Key1", KeyComparison.OrdinalIgnoreCase, true)]
    //[InlineData("key1[35]", "key1[35]", KeyComparison.Ordinal, true)]
    //[InlineData("key1[23]", "key1[34]", KeyComparison.Ordinal, false)]
    //[InlineData("key1[35]", "Key1[35]", KeyComparison.Ordinal, false)]
    //[InlineData("key1[35]", "Key1[35]", KeyComparison.OrdinalIgnoreCase, true)]
    //[InlineData("key1$label1", "key1$label1", KeyComparison.Ordinal, true)]
    //[InlineData("key1$label1", "key1$label2", KeyComparison.Ordinal, false)]
    //[InlineData("key1$label1", "key1$Label1", KeyComparison.Ordinal, false)]
    //[InlineData("key1$Label1", "Key1$label1", KeyComparison.OrdinalIgnoreCase, true)]
    //[InlineData("key1$label1[34]", "key1$label1[34]", KeyComparison.Ordinal, true)]
    //[InlineData("key1$label1[34]", "key1$label2", KeyComparison.Ordinal, false)]
    //[InlineData("key1$label1[23]", "key1$Label1[23]", KeyComparison.Ordinal, false)]
    //[InlineData("key1$Label1[23]", "Key1$label1[23]", KeyComparison.OrdinalIgnoreCase, true)]
    //public void TestKeySegmentEquality(string left, string right, KeyComparison comparison, bool result)
    //{
    //    Assert.Equal(result, KeySegment.Parse(left).Equals(KeySegment.Parse(right), comparison));
    //}


    

    //public void TestKeySegmentEqualityOperators(string left, string right, string opr, bool result)
    //{
    //    var ls = KeySegment.Parse(left);
    //    var rs = KeySegment.Parse(right);

    //    //var compare = 


    //}
}
