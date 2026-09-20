namespace Assimalign.Cohesion.Database.Hosting;

internal sealed record DatabaseApplicationComposition(
    DatabaseApplicationOptions Options,
    DatabaseApplicationContext Context,
    DatabaseApplicationOwnership Ownership);
