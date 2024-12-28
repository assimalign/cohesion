using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

using IO;

public class SizeTests
{
    [Fact(DisplayName = "Core (Tests): Success Size Calculation")]
    public void SuccessSizeCalculationTest()
    {
        var size = new Size(253928);

        Assert.Equal(253.928, size.Kilobytes);
        Assert.Equal(0.253928, size.Megabytes);
        Assert.Equal(0.000253928, size.Gigabytes);
        Assert.Equal(2.53928E-7, size.Terabytes);
    }

    [Fact]
    public void Test()
    {
        var size = Size.FromMegabytes(12.45);

        Assert.Equal(12450000, size.Length);
    }
}