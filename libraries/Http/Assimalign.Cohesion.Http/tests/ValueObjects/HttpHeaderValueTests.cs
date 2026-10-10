using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHeaderValueTests
{
    // Copying every value on every append allocates about 8 * n * n / 2 bytes: 10 GB for this many.
    // Appending in amortized constant time allocates about 1 MB.
    private const int manyRepeats = 50_000;
    private const long linearAllocationBound = 16L * 1024 * 1024;

    [Fact]
    public void Value_MultipleValues_ShouldMatchStringConversionAndToString()
    {
        // Arrange
        HttpHeaderValue value = new(new[] { "text/plain", "application/json" });

        // Act
        string implicitValue = value;

        // Assert
        value.Value.ShouldBe("text/plain,application/json");
        implicitValue.ShouldBe(value.Value);
        value.ToString().ShouldBe(value.Value);
    }

    [Fact]
    public void Value_NullAndEmptyEntries_ShouldSkipEmptySegments()
    {
        // Arrange
        HttpHeaderValue value = new(new string?[] { "text/plain", null, string.Empty, "application/json" });

        // Act
        string actual = value.Value;

        // Assert
        actual.ShouldBe("text/plain,application/json");
    }

    [Fact]
    public void Value_EmptyInstance_ShouldReturnEmptyStringAcrossAccessors()
    {
        // Arrange
        HttpHeaderValue value = HttpHeaderValue.Empty;

        // Act
        string implicitValue = value;

        // Assert
        value.Value.ShouldBe(string.Empty);
        implicitValue.ShouldBe(string.Empty);
        value.ToString().ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: The value should stay one reference wide")]
    public void SizeOf_OnHttpHeaderValue_ShouldBeOneReferenceWide()
    {
        // Arrange — every header collection is a dictionary of these, so a second field would widen every
        // entry of every request, response and trailer collection on every protocol.
        int referenceSize = IntPtr.Size;

        // Act
        int size = Unsafe.SizeOf<HttpHeaderValue>();

        // Assert
        size.ShouldBe(referenceSize);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: An append into a spare slot should allocate only a small holder")]
    public void Concat_OnAppendIntoSpareSlot_ShouldAllocateOnlyAHolder()
    {
        // Arrange — six values: their array has room for eight, so the seventh fills a spare slot. A warm-up
        // append from another value runs the same path first, so the measurement counts only Concat.
        HttpHeaderValue value = AppendAll("a", "b", "c", "d", "e", "f");
        _ = HttpHeaderValue.Concat(AppendAll("a", "b", "c", "d", "e", "f"), "warm-up");

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpHeaderValue appended = HttpHeaderValue.Concat(value, "g");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert — one object of a reference and an int: 32 bytes on 64-bit, 16 on 32-bit.
        appended.ToArray().ShouldBe(["a", "b", "c", "d", "e", "f", "g"]);
        allocated.ShouldBeLessThanOrEqualTo(4L * IntPtr.Size);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: Appending a value many times should allocate in proportion to the count")]
    public void Concat_OnManyRepeatedAppends_ShouldAllocateLinearly()
    {
        // Arrange — the values exist before the measurement starts, so it counts only what Concat allocates.
        string[] values = Enumerable.Range(0, manyRepeats).Select(static index => "v" + index).ToArray();
        HttpHeaderValue combined = default;

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();

        foreach (string value in values)
        {
            combined = HttpHeaderValue.Concat(combined, value);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        combined.Count.ShouldBe(manyRepeats);
        combined[0].ShouldBe("v0");
        combined[manyRepeats - 1].ShouldBe("v" + (manyRepeats - 1));
        allocated.ShouldBeLessThan(linearAllocationBound);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: Two values appended to one original should not see each other's")]
    public void Concat_OnTwoAppendsFromOneValue_ShouldKeepEachAppendSeparate()
    {
        // Arrange — five values: past the exact-array length, so the backing array has spare slots.
        HttpHeaderValue original = AppendAll("a", "b", "c", "d", "e");

        // Act
        HttpHeaderValue first = HttpHeaderValue.Concat(original, "x");
        HttpHeaderValue second = HttpHeaderValue.Concat(original, "y");
        HttpHeaderValue third = HttpHeaderValue.Concat(first, "z");

        // Assert
        original.ToArray().ShouldBe(["a", "b", "c", "d", "e"]);
        first.ToArray().ShouldBe(["a", "b", "c", "d", "e", "x"]);
        second.ToArray().ShouldBe(["a", "b", "c", "d", "e", "y"]);
        third.ToArray().ShouldBe(["a", "b", "c", "d", "e", "x", "z"]);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: A grown value should expose only its own values through every accessor")]
    public void Concat_OnGrownValue_ShouldExposeOnlyItsOwnValues()
    {
        // Arrange — a sibling append fills the slot just past the value's own, where an accessor that read
        // the whole backing array would find it.
        HttpHeaderValue value = AppendAll("a", "b", "c", "d", "e");
        _ = HttpHeaderValue.Concat(value, "sibling");
        HttpHeaderValue exact = new(new string?[] { "a", "b", "c", "d", "e" });
        IList<string?> list = value;

        // Act
        string?[]? converted = value;
        string?[] copied = new string?[5];
        list.CopyTo(copied, 0);
        List<string?> enumerated = [];

        foreach (string? item in value)
        {
            enumerated.Add(item);
        }

        // Assert
        value.Count.ShouldBe(5);
        value.Value.ShouldBe("a,b,c,d,e");
        converted.ShouldBe(exact.ToArray());
        copied.ShouldBe(exact.ToArray());
        enumerated.ShouldBe(exact.ToArray());
        list.IndexOf("sibling").ShouldBe(-1);
        list.Contains("sibling").ShouldBeFalse();
        value.Equals(exact).ShouldBeTrue();
        value.GetHashCode().ShouldBe(exact.GetHashCode());
        value.IsEmpty.ShouldBeFalse();
        HttpHeaderValue.IsNullOrEmpty(value).ShouldBeFalse();
        Should.Throw<IndexOutOfRangeException>(() => value[5]);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: Concurrent appends to one value should each keep their own value")]
    public void Concat_OnConcurrentAppendsFromOneValue_ShouldGiveEachAppendItsOwnValue()
    {
        // Arrange — a shared value with spare slots, appended to from many threads at once.
        HttpHeaderValue original = AppendAll("a", "b", "c", "d", "e");
        HttpHeaderValue[] results = new HttpHeaderValue[64];

        // Act
        Parallel.For(0, results.Length, index => results[index] = HttpHeaderValue.Concat(original, "v" + index));

        // Assert
        original.Count.ShouldBe(5);

        for (int index = 0; index < results.Length; index++)
        {
            results[index].ToArray().ShouldBe(["a", "b", "c", "d", "e", "v" + index]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderValue: Concatenating a single null value should keep the null")]
    public void Concat_OnSingleNullValue_ShouldKeepTheNull()
    {
        // Arrange
        HttpHeaderValue first = new("a");
        HttpHeaderValue second = new(new string?[] { null });

        // Act
        HttpHeaderValue combined = HttpHeaderValue.Concat(first, second);

        // Assert
        combined.Count.ShouldBe(2);
        combined[0].ShouldBe("a");
        combined[1].ShouldBeNull();
    }

    private static HttpHeaderValue AppendAll(params string[] values)
    {
        HttpHeaderValue combined = default;

        foreach (string value in values)
        {
            combined = HttpHeaderValue.Concat(combined, value);
        }

        return combined;
    }
}
