using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpSessionParityTests
{
    [Fact]
    public void IsAvailable_BeforeLoad_ShouldBeFalse()
    {
        HttpSession session = new("id");

        session.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task IsAvailable_AfterLoad_ShouldBeTrue()
    {
        HttpSession session = new("id");

        await session.LoadAsync();

        session.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Keys_ShouldReflectSetRemoveAndClear()
    {
        HttpSession session = new("id");

        session.Set("a", [1]);
        session.Set("b", [2]);
        session.Keys.OrderBy(static k => k).ShouldBe(new[] { "a", "b" });

        session.Remove("a");
        session.Keys.ShouldBe(new[] { "b" });

        session.Clear();
        session.Keys.ShouldBeEmpty();
    }

    [Fact]
    public void SetInt32_GetInt32_ShouldRoundTrip()
    {
        HttpSession session = new("id");

        session.SetInt32("count", 42);

        session.GetInt32("count").ShouldBe(42);
    }

    [Fact]
    public void SetInt32_NegativeValue_ShouldRoundTrip()
    {
        HttpSession session = new("id");

        session.SetInt32("delta", -123456);

        session.GetInt32("delta").ShouldBe(-123456);
    }

    [Fact]
    public void GetInt32_MissingKey_ShouldReturnNull()
    {
        HttpSession session = new("id");

        session.GetInt32("missing").ShouldBeNull();
    }

    [Fact]
    public void GetInt32_NonFourByteValue_ShouldReturnNull()
    {
        // A value that was not written as a 4-byte int must not be
        // misinterpreted as one.
        HttpSession session = new("id");
        session.Set("text", [1, 2, 3]);

        session.GetInt32("text").ShouldBeNull();
    }

    [Fact]
    public void TryGetInt32_ShouldRoundTrip()
    {
        HttpSession session = new("id");
        session.SetInt32("n", 7);

        bool found = session.TryGetInt32("n", out int value);

        found.ShouldBeTrue();
        value.ShouldBe(7);
    }

    [Fact]
    public void TryGetInt32_MissingKey_ShouldReturnFalse()
    {
        HttpSession session = new("id");

        session.TryGetInt32("missing", out int value).ShouldBeFalse();
        value.ShouldBe(0);
    }

    [Fact]
    public void SetString_GetString_ShouldRoundTrip()
    {
        HttpSession session = new("id");

        session.SetString("name", "cohesion");

        session.GetString("name").ShouldBe("cohesion");
    }

    [Fact]
    public void GetString_MissingKey_ShouldReturnNull()
    {
        HttpSession session = new("id");

        session.GetString("missing").ShouldBeNull();
    }
}
