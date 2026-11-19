namespace Assimalign.Cohesion.Sdk.Database.Tests;

internal sealed record MigrationNarrowRow(long Id, int? Value);
internal sealed record MigrationWideRow(long Id, long Value);
