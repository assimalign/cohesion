using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHeaderCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: The first cached field adds no allocation")]
    public void Add_FirstCachedField_ShouldNotAllocate()
    {
        // Arrange
        HttpHeaderCollection[] collections = CreateCollections();
        HttpHeaderKey date = HttpHeaderKey.Date;
        HttpHeaderValue value = "Sun, 11 Oct 2026 12:00:00 GMT";
        HttpHeaderCollection warmup = new();
        warmup.Add(date, value);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (HttpHeaderCollection collection in collections)
        {
            collection.Add(date, value);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0L);
        collections.ShouldAllBe(collection => collection.Count == 1);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: A cached first field does not grow ordinary field storage")]
    [InlineData(3)]
    [InlineData(7)]
    public void Add_CachedFieldBeforeOrdinaryFields_ShouldNotAddDictionaryAllocation(int fieldCount)
    {
        // Arrange
        _ = MeasureInsertions(includeCachedField: false, fieldCount);
        _ = MeasureInsertions(includeCachedField: true, fieldCount);

        // Act
        long ordinaryFields = MeasureInsertions(includeCachedField: false, fieldCount);
        long withCachedField = MeasureInsertions(includeCachedField: true, fieldCount);

        // Assert
        withCachedField.ShouldBe(ordinaryFields);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: Header mutations preserve case-insensitive and empty-value behavior")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Add_CaseInsensitiveKeysAndEmptyValues_ShouldPreserveCollectionSemantics(int constructor)
    {
        // Arrange
        HttpHeaderCollection headers = CreateCollection(constructor);

        // Act
        headers.Add("Date", "original");
        headers["date"] = "replacement";
        headers.Add("X-Empty", HttpHeaderValue.Empty);
        headers.Add("X-Other", "other");

        // Assert
        headers.Count.ShouldBe(3);
        headers["DATE"].Value.ShouldBe("replacement");
        headers.ContainsKey("x-empty").ShouldBeTrue();
        headers.TryGetValue("X-Empty", out HttpHeaderValue empty).ShouldBeTrue();
        empty.ShouldBe(HttpHeaderValue.Empty);
        Should.Throw<ArgumentException>(() => headers.Add("DATE", "duplicate"));
        Should.Throw<ArgumentException>(() => headers.Add("x-other", "duplicate"));
        headers["x-empty"] = HttpHeaderValue.Empty;
        headers.Remove("DATE");
        headers.Count.ShouldBe(1);
        headers["missing"].ShouldBe(HttpHeaderValue.Empty);
        headers.Add("X-New", "new");
        headers.Count.ShouldBe(2);
        headers["x-new"].Value.ShouldBe("new");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: A read-only view follows growth, removal and reuse of the original")]
    public void AsReadOnly_EmptySourceThenMutations_ShouldExposeLiveCollection()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        HttpHeaderCollection view = headers.AsReadOnly();

        // Act
        headers.Add("Date", "cached");
        headers.Add("Content-Type", "text/plain");
        headers.Add("X-Other", "other");
        headers.Remove("Date");
        headers["X-New"] = "new";

        // Assert
        view.Count.ShouldBe(3);
        view.ContainsKey("Date").ShouldBeFalse();
        view["X-New"].Value.ShouldBe("new");
        view.ToArray().ShouldBe(headers.ToArray());
        view.IsReadOnly.ShouldBeTrue();
        view.AsReadOnly().ShouldBeSameAs(view);
        Should.Throw<InvalidOperationException>(() => view.Add("X", "value"));
        Should.Throw<InvalidOperationException>(() => view["X"] = HttpHeaderValue.Empty);
        Should.Throw<InvalidOperationException>(() => view.Remove("missing"));
        Should.Throw<InvalidOperationException>(() => view.Clear());
        headers.Clear();
        view.Count.ShouldBe(0);
        view.ShouldBeEmpty();
        headers["Date"] = "later";
        view["Date"].Value.ShouldBe("later");
        view.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: A supplied dictionary and read-only view remain live aliases")]
    public void Constructor_SuppliedDictionary_ShouldShareAllMutations()
    {
        // Arrange
        Dictionary<HttpHeaderKey, HttpHeaderValue> store = new();
        HttpHeaderCollection headers = new(store);
        HttpHeaderCollection view = headers.AsReadOnly();

        // Act
        store.Add("Date", "external");
        headers.Add("Content-Type", "text/plain");
        store["date"] = "updated";

        // Assert
        store.Count.ShouldBe(2);
        store["Content-Type"].Value.ShouldBe("text/plain");
        headers["Date"].Value.ShouldBe("updated");
        view["Date"].Value.ShouldBe("updated");
        view.ToArray().ShouldBe(store.ToArray());
        headers.Remove("Date");
        store.ContainsKey("Date").ShouldBeFalse();
        store.Clear();
        view.ShouldBeEmpty();
        headers.Add("X-Later", "later");
        store["X-Later"].Value.ShouldBe("later");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: All enumeration paths expose the same fields")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GetEnumerator_CollectionWithMultipleFields_ShouldExposeEveryField(int constructor)
    {
        // Arrange
        HttpHeaderCollection headers = CreateCollection(constructor);
        headers.Add("Date", "cached");
        headers.Add("X-First", "first");
        headers.Add("X-Second", "second");

        // Act
        KeyValuePair<HttpHeaderKey, HttpHeaderValue>[] generic = headers.ToArray();
        KeyValuePair<HttpHeaderKey, HttpHeaderValue>[] nonGeneric = ((IEnumerable)headers)
            .Cast<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>().ToArray();

        // Assert
        generic.Length.ShouldBe(3);
        nonGeneric.ShouldBe(generic);
        headers.AsReadOnly().ToArray().ShouldBe(generic);
        generic.Select(field => field.Key.Value).ShouldBe(new[] { "Date", "X-First", "X-Second" });
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: Adding a new field invalidates active enumeration")]
    public void GetEnumerator_AddDuringEnumeration_ShouldRejectMutation()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers.Add("Date", "cached");
        using IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> enumerator = headers.GetEnumerator();

        // Act
        headers.Add("Content-Type", "text/plain");

        // Assert
        Should.Throw<InvalidOperationException>(() => enumerator.MoveNext());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderCollection: Replacing or removing fields does not invalidate enumeration")]
    public void GetEnumerator_ReplaceAndRemoveBeforeMove_ShouldObserveRemainingFields()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers.Add("Date", "cached");
        headers.Add("X-Removed", "removed");
        headers.Add("X-Kept", "kept");
        using IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> enumerator = headers.GetEnumerator();

        // Act
        headers["Date"] = "replacement";
        headers.Remove("X-Removed");

        // Assert
        enumerator.MoveNext().ShouldBeTrue();
        enumerator.Current.Value.Value.ShouldBe("replacement");
        enumerator.MoveNext().ShouldBeTrue();
        enumerator.Current.Key.Value.ShouldBe("X-Kept");
        headers.Clear();
        enumerator.MoveNext().ShouldBeFalse();
    }

    private static HttpHeaderCollection CreateCollection(int constructor) => constructor switch
    {
        0 => new HttpHeaderCollection(),
        1 => new HttpHeaderCollection(3),
        2 => new HttpHeaderCollection(new Dictionary<HttpHeaderKey, HttpHeaderValue>()),
        _ => new HttpHeaderCollection((Dictionary<HttpHeaderKey, HttpHeaderValue>?)null)
    };

    private static HttpHeaderCollection[] CreateCollections()
    {
        HttpHeaderCollection[] collections = new HttpHeaderCollection[1_000];
        for (int i = 0; i < collections.Length; i++)
        {
            collections[i] = new HttpHeaderCollection();
        }
        return collections;
    }

    private static long MeasureInsertions(bool includeCachedField, int fieldCount)
    {
        HttpHeaderCollection[] collections = CreateCollections();
        HttpHeaderKey[] keys = ["X-One", "X-Two", "X-Three", "X-Four", "X-Five", "X-Six", "X-Seven"];
        HttpHeaderKey date = HttpHeaderKey.Date;
        HttpHeaderValue value = "cached";
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (HttpHeaderCollection collection in collections)
        {
            if (includeCachedField)
            {
                collection.Add(date, value);
            }
            for (int i = 0; i < fieldCount; i++)
            {
                collection.Add(keys[i], value);
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
