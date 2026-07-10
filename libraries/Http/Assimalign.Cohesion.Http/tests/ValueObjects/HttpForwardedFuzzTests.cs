using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Hostile-input / fuzz-style tests over the forwarded-header primitives. Every parser must be
/// <em>total</em> on adversarial input: it returns a deterministic <see langword="bool"/> and never
/// throws, never hangs, and never overflows the stack — no matter how malformed, truncated, or
/// pathologically large the input is. These are the inputs a proxy chain (hostile or buggy) can put
/// in front of the trust-model middleware (issue #778), so getting deterministic rejection here is a
/// security property, not a nicety.
/// </summary>
public class HttpForwardedFuzzTests
{
    public static IEnumerable<object[]> HostileInputs()
    {
        var inputs = new List<string>
        {
            // Empty / whitespace / lone delimiters.
            "", " ", "\t", "\n", "\r\n",
            "=", "==", "===", ";", ";;;", ",", ",,,", ":", "::::", "\"", "\"\"",

            // Truncated / unterminated structure.
            "for", "for=", "by=", "host=", "proto=",
            "for=;by=;host=;proto=",
            "for=\"", "for=\"\\", "for=\"\\\"", "for=\"unterminated",
            "[", "]", "[]", "][", "for=[", "for=[]", "for=[::1", "for=::1]",

            // Ports gone wrong.
            "for=1.2.3.4:", "for=:80", "for=1.2.3.4:99999999999", "for=1.2.3.4:-1",
            "[2001:db8::1]:", "[2001:db8::1]:abc",

            // Registered-name confusion / repeats.
            "for=1.2.3.4;for=5.6.7.8;for=9.10.11.12",
            "FOR=1.2.3.4;FoR=5.6.7.8",

            // Whitespace / non-token content.
            "for = 1.2.3.4", "proto=ht tp", "bad name=value", "for\t=\t1.2.3.4",

            // Embedded control / null / high-plane characters.
            "\0", "for=\0", "for=a\0b", "host=", "for=￿", "🙂=🙂", "for=🙂",

            // Mixed valid + junk in a list.
            "for=1.2.3.4, , , , for=5.6.7.8, bad,",
            "203.0.113.1, , 70.41.3.18, ,,",

            // Pathologically large inputs (must terminate, must not overflow).
            new string('a', 5000),
            new string('[', 2000),
            new string(']', 2000),
            new string(';', 2000),
            new string(',', 2000),
            new string(':', 2000),
            "for=" + new string('9', 5000),
            "\"" + new string('a', 5000),
            "for=\"" + new string('\\', 2000) + "\"",
            "for=1.2.3.4" + new string(':', 500),
            string.Join(",", System.Linq.Enumerable.Repeat("for=1.2.3.4", 500)),
        };

        foreach (string input in inputs)
        {
            yield return new object[] { input };
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedFuzz: HttpForwardedNode should be total and deterministic")]
    [MemberData(nameof(HostileInputs))]
    public void Node_ShouldBeTotalAndDeterministic(string input)
    {
        bool first = false;
        bool second = false;
        HttpForwardedNode firstNode = default;
        HttpForwardedNode secondNode = default;

        Should.NotThrow(() => first = HttpForwardedNode.TryParse(input, out firstNode));
        Should.NotThrow(() => second = HttpForwardedNode.TryParse(input, out secondNode));

        second.ShouldBe(first);
        if (first)
        {
            firstNode.ToString().ShouldBe(secondNode.ToString());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedFuzz: HttpForwardedElement should be total and deterministic")]
    [MemberData(nameof(HostileInputs))]
    public void Element_ShouldBeTotalAndDeterministic(string input)
    {
        bool first = false;
        bool second = false;
        HttpForwardedElement firstElement = default;
        HttpForwardedElement secondElement = default;

        Should.NotThrow(() => first = HttpForwardedElement.TryParse(input, out firstElement));
        Should.NotThrow(() => second = HttpForwardedElement.TryParse(input, out secondElement));

        second.ShouldBe(first);
        if (first)
        {
            firstElement.Serialize().ShouldBe(secondElement.Serialize());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedFuzz: HttpForwardedElementCollection should be total and deterministic")]
    [MemberData(nameof(HostileInputs))]
    public void Collection_ShouldBeTotalAndDeterministic(string input)
    {
        bool first = false;
        bool second = false;
        HttpForwardedElementCollection firstList = default;
        HttpForwardedElementCollection secondList = default;

        Should.NotThrow(() => first = HttpForwardedElementCollection.TryParse(input, out firstList));
        Should.NotThrow(() => second = HttpForwardedElementCollection.TryParse(input, out secondList));

        second.ShouldBe(first);
        if (first)
        {
            firstList.Serialize().ShouldBe(secondList.Serialize());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedFuzz: HttpForwardedValues should be total and deterministic")]
    [MemberData(nameof(HostileInputs))]
    public void Values_ShouldBeTotalAndDeterministic(string input)
    {
        bool first = false;
        bool second = false;
        HttpForwardedValues firstValues = default;
        HttpForwardedValues secondValues = default;

        Should.NotThrow(() => first = HttpForwardedValues.TryParse(input, out firstValues));
        Should.NotThrow(() => second = HttpForwardedValues.TryParse(input, out secondValues));

        second.ShouldBe(first);
        if (first)
        {
            firstValues.Serialize().ShouldBe(secondValues.Serialize());
        }
    }
}
