using System;
using Xunit;

namespace System.Tests;

public class SizeTests
{
    [Fact(DisplayName = "Core (Tests): Kilobyte Size Calculation")]
    public void CalculateKilobyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1000, 1)), size.Kilobytes);
    }

    [Fact(DisplayName = "Core (Tests): Kibibyte Size Calculation")]
    public void CalculateKibibyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1024, 1)), size.Kibibytes);
    }

    [Fact(DisplayName = "Core (Tests): Megabyte Size Calculation")]
    public void CalculateMegabyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1000, 2)), size.Megabytes);
    }

    [Fact(DisplayName = "Core (Tests): Mebibyte Size Calculation")]
    public void CalculateMebibyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1024, 2)), size.Mebibytes);
    }

    [Fact(DisplayName = "Core (Tests): Gigabyte Size Calculation")]
    public void CalculateGigabyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1000, 3)), size.Gigabytes);
    }

    [Fact(DisplayName = "Core (Tests): Gibibyte Size Calculation")]
    public void CalculateGibibyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1024, 3)), size.Gibibytes);
    }

    [Fact(DisplayName = "Core (Tests): Terabyte Size Calculation")]
    public void CalculateTerabyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1000, 4)), size.Terabytes);
    }

    [Fact(DisplayName = "Core (Tests): Tebibyte Size Calculation")]
    public void CalculateTebibyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1024, 4)), size.Tebibytes);
    }

    [Fact(DisplayName = "Core (Tests): Petabyte Size Calculation")]
    public void CalculatePetabyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1000, 5)), size.Petabytes);
    }

    [Fact(DisplayName = "Core (Tests): Petabyte Size Calculation")]
    public void CalculatePebibyteSizeTest()
    {
        var random = new Random();

        var value = random.NextInt64(100, long.MaxValue);

        var size = new Size(value);

        Assert.Equal((value / Math.Pow(1024, 5)), size.Pebibytes);
    }

    [Fact(DisplayName = "Core (Tests): Size Greater/Less Than")]
    public void GreaterThanSizeTest()
    {
        var size1 = new Size(100);
        var size2 = new Size(101);

        Assert.True(size2 > size1);
        Assert.False(size2 < size1);
        Assert.True(size1 < size2);
    }

    [Fact(DisplayName = "Core (Tests): Size FromMegabytes()")]
    public void SizeFromMegabytesTest()
    {
        Assert.Equal(12450000, Size.FromMegabytes(12.45).Length);
    }
}