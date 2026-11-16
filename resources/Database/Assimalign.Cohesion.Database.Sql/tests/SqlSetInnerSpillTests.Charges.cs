namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSetInnerSpillTests
{
    // Exact unspilled charges from the independently built batch base.
    private static (long Grant, long Peak) BaseFittingMemory(int shape) => shape switch
    {
        0 => (1048576, 690048),
        1 => (1048576, 550304),
        2 => (524288, 444000),
        3 => (524288, 403456),
        4 => (524288, 379856),
        5 => (524288, 518080),
        6 => (1048576, 720896),
        7 => (524288, 524288),
        8 => (2097152, 1228800),
        9 => (1048576, 590304),
        10 => (1048576, 589488),
        11 => (1048576, 590304),
        12 => (262144, 173024),
        13 => (262144, 181576),
        14 => (1048576, 589488),
        15 => (262144, 189952),
        16 => (2097152, 1108464),
        17 => (1048576, 557056),
        18 => (524288, 393216),
        19 => (16777216, 9358752),
        _ => throw new System.ArgumentOutOfRangeException(nameof(shape)),
    };
}
