using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class SqlHeapPool
{
    internal sealed class HashPoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode(), _chars = ArrayPool<char>.Shared.GetHashCode(),
            _objects = ArrayPool<object>.Shared.GetHashCode(), _ints = ArrayPool<int>.Shared.GetHashCode(), _values = ArrayPool<SqlValue>.Shared.GetHashCode();
        // Element widths of every pool the listener can size. Besides the pools these tests and the engine rent
        // from directly, LINQ materialization and the runtime rent from ArrayPool<T>.Shared for many T; a trim
        // in a pool missing here is unsized and forces a remeasure.
        private static readonly Dictionary<int, int> _widths = Widths();
        internal long TrimEvents, UnsizedTrims;
        internal long Trimmed, Rented, Allocated, Dropped, Unknown;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Informational); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            // Count every pool and element type, before any payload sizing/filtering.
            if (args.EventId == 4) { Interlocked.Increment(ref TrimEvents); }
            if (args.EventId is not (1 or 2 or 4 or 6)) { return; }
            if (args.Payload is null || args.Payload.Count < 3)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = _widths.TryGetValue(pool, out int known) ? known : 0;
            if (width == 0)
            {
                Interlocked.Increment(ref Unknown);
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 4) { Interlocked.Add(ref Trimmed, size); return; }
            // Rent, allocation and drop counters keep following only their original five pools.
            if (pool != _bytes && pool != _chars && pool != _objects && pool != _ints && pool != _values) { return; }
            if (args.EventId == 1) { Interlocked.Add(ref Rented, size); }
            if (args.EventId == 2) { Interlocked.Add(ref Allocated, size); }
            if (args.EventId == 6) { Interlocked.Add(ref Dropped, size); }
        }

        private static Dictionary<int, int> Widths()
        {
            var widths = new Dictionary<int, int>();
            void Add<T>() => widths[ArrayPool<T>.Shared.GetHashCode()] = Unsafe.SizeOf<T>();
            Add<byte>(); Add<sbyte>(); Add<bool>(); Add<char>(); Add<short>(); Add<ushort>(); Add<int>(); Add<uint>();
            Add<long>(); Add<ulong>(); Add<nint>(); Add<nuint>(); Add<float>(); Add<double>(); Add<decimal>(); Add<Half>();
            Add<Guid>(); Add<DateTime>(); Add<DateTimeOffset>(); Add<TimeSpan>(); Add<DateOnly>(); Add<TimeOnly>();
            Add<object>(); Add<string>(); Add<Type>(); Add<MemberInfo>(); Add<object[]>(); Add<string[]>(); Add<byte[]>();
            Add<SqlValue>(); Add<SqlValue[]>();
            // Engine and test types, which LINQ materialization also pools: every non-generic type of the
            // loaded Cohesion assemblies. Reflection is fine here; test hosts are not trimmed or AOT-compiled.
            MethodInfo sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly.GetName().Name?.StartsWith("Assimalign.Cohesion", StringComparison.Ordinal) != true) { continue; }
                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException failure) { types = failure.Types; }
                foreach (Type? type in types)
                {
                    if (type is null || type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || type.IsAbstract && type.IsSealed || type.Name.Contains('<')) { continue; }
                    try
                    {
                        object pool = typeof(ArrayPool<>).MakeGenericType(type).GetProperty(nameof(ArrayPool<byte>.Shared))!.GetValue(null)!;
                        widths.TryAdd(pool.GetHashCode(), type.IsValueType ? (int)sizeOf.MakeGenericMethod(type).Invoke(null, null)! : IntPtr.Size);
                    }
                    catch (Exception failure) when (failure is ArgumentException or TypeLoadException or TargetInvocationException or NotSupportedException) { }
                }
            }
            return widths;
        }
    }

    private static List<byte[]> PinWindowPool(HashPoolEvents events)
    {
        // Hold pre-existing pooled page images across both baselines and every sample.
        // A new allocation proves that each relevant shared bucket has been drained.
        var held = new List<byte[]>();
        foreach (int size in new[] { 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 })
        {
            long allocated = events.Allocated;
            int rents = 0;
            do { held.Add(ArrayPool<byte>.Shared.Rent(size)); rents++; }
            while (events.Allocated == allocated && rents < 1024);
            events.Allocated.ShouldBeGreaterThan(allocated);
        }
        return held;
    }

    internal sealed class WindowPoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];
        internal void Drain(HashPoolEvents events) => _buffers.AddRange(PinWindowPool(events));
        public void Dispose()
        { foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); } _buffers.Clear(); }
    }
}
