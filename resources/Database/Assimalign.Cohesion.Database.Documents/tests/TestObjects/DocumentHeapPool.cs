using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Shouldly;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapPool
{
    internal sealed class PoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode();
        // Element widths of every pool the listener can size. Besides the pools these tests and the engine rent
        // from directly, LINQ materialization and the runtime rent from ArrayPool<T>.Shared for many T; a trim
        // in a pool missing here is unsized and forces a remeasure.
        private static readonly Dictionary<int, int> _widths = Widths();
        internal long TrimEvents, Trimmed, UnsizedTrims;
        internal long Allocated;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Informational); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            // Count every pool and element type, before any payload sizing/filtering.
            if (args.EventId == 4) { Interlocked.Increment(ref TrimEvents); }
            if (args.EventId is not (2 or 4)) { return; }
            if (args.Payload is null || args.Payload.Count < 3)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = _widths.TryGetValue(pool, out int known) ? known : 0;
            if (width == 0)
            {
                if (args.EventId == 4) { Interlocked.Increment(ref UnsizedTrims); }
                return;
            }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 2)
            {
                if (pool == _bytes) { Interlocked.Add(ref Allocated, size); }
            }
            else { Interlocked.Add(ref Trimmed, size); }
        }

        private static Dictionary<int, int> Widths()
        {
            var widths = new Dictionary<int, int>();
            void Add<T>() => widths[ArrayPool<T>.Shared.GetHashCode()] = Unsafe.SizeOf<T>();
            Add<byte>(); Add<sbyte>(); Add<bool>(); Add<char>(); Add<short>(); Add<ushort>(); Add<int>(); Add<uint>();
            Add<long>(); Add<ulong>(); Add<nint>(); Add<nuint>(); Add<float>(); Add<double>(); Add<decimal>(); Add<Half>();
            Add<Guid>(); Add<DateTime>(); Add<DateTimeOffset>(); Add<TimeSpan>(); Add<DateOnly>(); Add<TimeOnly>();
            Add<object>(); Add<string>(); Add<Type>(); Add<MemberInfo>(); Add<object[]>(); Add<string[]>(); Add<byte[]>();
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

    internal sealed class PoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];

        internal void Drain(PoolEvents events)
        {
            // JSON metadata for the 20 MiB document can occupy a 32 MiB bucket.
            for (int size = 8192; size <= 32 * 1024 * 1024; size *= 2)
            {
                long allocated = events.Allocated;
                int rents = 0;
                do { _buffers.Add(ArrayPool<byte>.Shared.Rent(size)); rents++; }
                while (events.Allocated == allocated && rents < 1024);
                events.Allocated.ShouldBeGreaterThan(allocated);
            }
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); }
            _buffers.Clear();
        }
    }
}
