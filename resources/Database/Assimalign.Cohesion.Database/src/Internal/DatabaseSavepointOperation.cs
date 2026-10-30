namespace Assimalign.Cohesion.Database.Internal;

internal enum DatabaseSavepointOperation : byte
{
    Create,
    Release,
    Rollback,
}
