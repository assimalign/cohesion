using System;
using System.Collections;

using NetLiteTypes = Amqp.Types;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Compares values as AMQPNetLite decodes them: binaries by content, lists, arrays and maps by their items,
/// described values by descriptor and value, and everything else by type and <see cref="object.Equals(object)"/>.
/// </summary>
internal static class AmqpNetLiteValues
{
    public static bool AreEqual(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return expected switch
        {
            byte[] binary => actual is byte[] other && binary.AsSpan().SequenceEqual(other),
            NetLiteTypes.DescribedValue described => actual is NetLiteTypes.DescribedValue other
                && AreEqual(described.Descriptor, other.Descriptor)
                && AreEqual(described.Value, other.Value),
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
