using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal static class ThrowUtility
{

    #region General Exceptions
    [DoesNotReturn]
    public static void ThrowArgumentException() => throw new ArgumentException();

    [DoesNotReturn]
    public static void ThrowArgumentException(string message) => throw new ArgumentException(message);

    [DoesNotReturn]
    public static void ThrowArgumentException(string message, Exception innerException) => throw new ArgumentException(message, innerException);

    [DoesNotReturn]
    public static void ThrowArgumentNullException(string paramName) => throw new ArgumentNullException(paramName);
    #endregion

    public static void InvalidHttpPath(string message) => 
        throw new HttpInvalidPathException(message);
    internal static void InvalidHttpMethod(string method) => 
        throw new HttpInvalidMethodException($"The provided method is invalid: '{method}'. A method can only contain alphanumeric characters.");
}
