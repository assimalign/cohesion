using System;

namespace Assimalign.Cohesion.Resilience.Internal;

#pragma warning disable CA5394 // Do not use insecure randomness

internal static class RandomUtil
{
    public static double NextDouble() => Random.Shared.NextDouble();
    public static int Next(int maxValue) => Random.Shared.Next(maxValue);
}
