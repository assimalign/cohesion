namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlAppliedSchemaHistoryEntry(string ContentHash, ulong FirstAppliedGeneration, ulong LastAppliedGeneration);
