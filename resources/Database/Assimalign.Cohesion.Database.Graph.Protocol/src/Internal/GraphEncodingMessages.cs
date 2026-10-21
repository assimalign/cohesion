namespace Assimalign.Cohesion.Database.Graph.Protocol.Internal;

internal readonly struct GraphEncodingMessages : IGraphEncodingMessages
{
    public static string CompositeLimit => ErrorMessages.CompositeLimit;
    public static string CompositeMalformed => ErrorMessages.CompositeMalformed;
    public static string CompositeRequired => ErrorMessages.CompositeRequired;
}
