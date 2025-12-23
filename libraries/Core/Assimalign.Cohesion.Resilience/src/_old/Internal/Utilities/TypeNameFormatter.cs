using System;

namespace Assimalign.Cohesion.Resilience.Internal;

internal static class TypeNameFormatter
{
    private const int GenericSuffixLength = 2;

    public static string Format(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var args = type.GetGenericArguments();
        if (args.Length != 1)
        {
            return type.Name;
        }
        var nameNoAirity = type.Name[..(type.Name.Length - GenericSuffixLength)];

        return $"{nameNoAirity}<{Format(args[0])}>";
    }
}
