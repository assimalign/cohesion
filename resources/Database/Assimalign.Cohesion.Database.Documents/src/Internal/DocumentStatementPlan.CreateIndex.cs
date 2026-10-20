using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentCreateIndexPlan(DocumentCollectionMetadata Collection, string IndexName, IReadOnlyList<string> Paths, bool IsUnique, bool IfNotExists) : DocumentStatementPlan;
