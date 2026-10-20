using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Graph.Protocol.Tests.TestObjects;

internal readonly struct CountingGraphEncodingMessages : IGraphEncodingMessages
{
    [ThreadStatic]
    private static int _lookupCount;

    internal static int LookupCount => _lookupCount;
    internal static void Reset() => _lookupCount = 0;

    public static string CompositeLimit => Lookup(nameof(CompositeLimit));
    public static string CompositeMalformed => Lookup(nameof(CompositeMalformed));
    public static string CompositeRequired => Lookup(nameof(CompositeRequired));

    private static string Lookup(string key)
    {
        _lookupCount++;
        return key + ":" + CultureInfo.CurrentUICulture.Name;
    }
}
