using System.IO;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Resource lookup stays in the engine and runs only on the linked algorithm's throw paths.
internal static class GraphCompositeEncoding
{
    internal static int Measure(object? value) => GraphValueEncoding.Measure<GraphEncodingMessages>(value);
    internal static int ScalarLength(object? value) => GraphValueEncoding.ScalarLength<GraphEncodingMessages>(value);
    internal static void Write(BinaryWriter writer, object? value, int depth, ref int count, DatabaseKeyWriter scalarWriter)
        => GraphValueEncoding.Write<GraphEncodingMessages>(writer, value, depth, ref count, scalarWriter);

}
