using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Internal;

internal static class RandomProvider
{
    [ThreadStatic]
    static Random? _random;

    [ThreadStatic]
    static XorShift64? _xorShift;

    // this random is async-unsafe, be careful to use.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Random GetRandom()
    {
        if (_random == null)
        {
            _random = CreateRandom();
        }
        return _random;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Random CreateRandom()
    {
        using (var rng = RandomNumberGenerator.Create())
        {
            // Span<byte> buffer = stackalloc byte[sizeof(int)];
            var buffer = new byte[sizeof(int)];
            rng.GetBytes(buffer);
            var seed = BitConverter.ToInt32(buffer, 0);
            return new Random(seed);
        }
    }

    // this random is async-unsafe, be careful to use.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static XorShift64 GetXorShift64()
    {
        if (_xorShift == null)
        {
            _xorShift = CreateXorShift64();
        }
        return _xorShift;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static XorShift64 CreateXorShift64()
    {
        using (var rng = RandomNumberGenerator.Create())
        {
            // Span<byte> buffer = stackalloc byte[sizeof(UInt64)];
            var buffer = new byte[sizeof(UInt64)];
            rng.GetBytes(buffer);
            var seed = BitConverter.ToUInt64(buffer, 0);
            return new XorShift64(seed);
        }
    }
}