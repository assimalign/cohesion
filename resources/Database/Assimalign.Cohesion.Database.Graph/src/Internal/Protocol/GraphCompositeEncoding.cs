using System.IO;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Resource lookup stays in the engine, at call time, rather than binding the linked
// algorithm to another assembly's generated resource accessor.
internal static class GraphCompositeEncoding
{
    internal static int Measure(object? value) => GraphValueEncoding.Measure(value, Messages);
    internal static int ScalarLength(object? value) => GraphValueEncoding.ScalarLength(value, Messages);
    internal static void Write(BinaryWriter writer, object? value, int depth, ref int count, DatabaseKeyWriter scalarWriter)
        => GraphValueEncoding.Write(writer, value, depth, ref count, scalarWriter, Messages);

    private static GraphValueEncoding.Messages Messages => new(
        ErrorMessages.CompositeLimit, ErrorMessages.CompositeMalformed, ErrorMessages.CompositeRequired);
}