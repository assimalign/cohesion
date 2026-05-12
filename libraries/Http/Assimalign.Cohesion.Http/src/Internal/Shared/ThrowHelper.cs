namespace Assimalign.Cohesion.Internal;

using Assimalign.Cohesion.Http.Internal;

internal static class ThrowHelper
{
    public static void InvalidHttpPath(string message) =>
        throw new HttpInvalidPathException(message);

    public static void InvalidHttpMethod(string method) =>
        throw new HttpInvalidMethodException($"The provided method is invalid: '{method}'.");
}
