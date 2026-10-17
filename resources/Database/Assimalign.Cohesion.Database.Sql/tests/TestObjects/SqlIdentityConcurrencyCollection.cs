using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Serializes tests that exercise identity allocation races.</summary>
[CollectionDefinition("SqlIdentityConcurrency", DisableParallelization = true)]
public sealed class SqlIdentityConcurrencyCollection
{
}
