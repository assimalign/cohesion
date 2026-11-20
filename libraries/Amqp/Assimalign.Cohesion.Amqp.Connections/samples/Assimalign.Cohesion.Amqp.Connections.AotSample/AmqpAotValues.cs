using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// Compares a value the guard sent with the value that arrived, structurally: a list, map, typed array, AMQP array,
/// binary or described value by its contents, and anything else by its own equality and CLR type.
/// </summary>
internal static class AmqpAotValues
{
    /// <summary>
    /// Returns whether <paramref name="actual"/> is the decoded form of <paramref name="expected"/>.
    /// </summary>
    public static bool AreEqual(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return expected switch
        {
            byte[] binary => actual is byte[] other && binary.AsSpan().SequenceEqual(other),
            ReadOnlyMemory<byte> memory => actual is ReadOnlyMemory<byte> other && memory.Span.SequenceEqual(other.Span),
            AmqpDescribedValue described => actual is AmqpDescribedValue other
                && AreEqual(described.Descriptor, other.Descriptor)
                && AreEqual(described.Value, other.Value),
            AmqpArray array => actual is AmqpArray other
                && array.ElementType == other.ElementType
                && AreEqual(array.Descriptor, other.Descriptor)
                && ElementsEqual(array.Elements, other.Elements),
            IDictionary map => actual is IDictionary other && MapsEqual(map, other),
            IList list => actual.GetType() == expected.GetType() && ListsEqual(list, (IList)actual),
            _ => actual.GetType() == expected.GetType() && expected.Equals(actual)
        };
    }

    private static bool ListsEqual(IList expected, IList actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (!AreEqual(expected[index], actual[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ElementsEqual(IReadOnlyList<object?> expected, IReadOnlyList<object?> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (!AreEqual(expected[index], actual[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MapsEqual(IDictionary expected, IDictionary actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        foreach (DictionaryEntry entry in expected)
        {
            if (!actual.Contains(entry.Key) || !AreEqual(entry.Value, actual[entry.Key]))
            {
                return false;
            }
        }

        return true;
    }
}
