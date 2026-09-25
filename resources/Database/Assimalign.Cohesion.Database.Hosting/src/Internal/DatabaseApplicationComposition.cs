namespace Assimalign.Cohesion.Database.Hosting.Internal;

internal sealed record DatabaseApplicationComposition(
    DatabaseApplicationOptions Options,
    DatabaseApplicationContext Context,
    DatabaseApplicationOwnership Ownership);
