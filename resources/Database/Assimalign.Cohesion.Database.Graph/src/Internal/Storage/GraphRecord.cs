using System;

using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal readonly record struct GraphRecord(byte Kind, ulong Id, StoredGraphNode? Node = null,
    StoredGraphRelationship? Relationship = null, string? Label = null, string? PropertyKey = null, ulong Revision = 0, Guid TypeId = default);
