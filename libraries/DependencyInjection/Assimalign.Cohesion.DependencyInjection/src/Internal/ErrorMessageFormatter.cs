using System;
using System.Globalization;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal static class ErrorMessageFormatter
{
    internal static string GetTryAddIndistinguishableTypeToEnumerableExceptionMessage(Type p1, Type p2)
    {
        return Format(ErrorMessages.TryAddIndistinguishableTypeToEnumerable, p1, p2);
    }
    internal static string GetCallSiteTypeNotSupportedExceptionMessage(Type p1)
    {
        return Format(ErrorMessages.CallSiteTypeNotSupported, p1);
    }
    internal static string GetNoServiceRegisteredExceptionMessage(Type p1)
    {
        return Format(ErrorMessages.NoServiceRegistered, p1);
    }
    internal static string GetAsyncDisposableServiceDisposeExceptionMessage(string p1)
    {
        return Format(ErrorMessages.AsyncDisposableServiceDispose, p1);
    }
    internal static string GetDirectScopedResolvedFromRootExceptionMessage(Type t1, string t2)
    {
        return Format(ErrorMessages.DirectScopedResolvedFromRootException, t1, t2);
    }
    internal static string GetScopedResolvedFromRootExceptionMessage(Type t1, Type t2, string t3)
    {
        return Format(ErrorMessages.ScopedResolvedFromRootException, t1, t2, t3);
    }
    internal static string GetScopedInSingletonExceptionMessage(Type t1, Type t2, string t3, string t4)
    {
        return Format(ErrorMessages.ScopedInSingletonException, t1, t2, t3, t4);
    }
    internal static string GetCannotResolveServiceExceptionMessage(Type parameterType, Type implementationType)
    {
        return Format(ErrorMessages.CannotResolveService, parameterType, implementationType);
    }
    internal static string GetUnableToActivateTypeExceptionMessage(Type implementationType)
    {
        return Format(ErrorMessages.UnableToActivateTypeException, implementationType);
    }
    internal static string GetAmbiguousConstructorExceptionMessage(Type implementationType)
    {
        return Format(ErrorMessages.AmbiguousConstructorException, implementationType);
    }
    internal static string GetNoConstructorMatchExceptionMessage(Type implementationType)
    {
        return Format(ErrorMessages.NoConstructorMatch, implementationType);
    }
    internal static string GetTrimmingAnnotationsDoNotMatch_NewConstraintExceptionMessage(Type implementationType, Type serviceType)
    {
        return Format(ErrorMessages.TrimmingAnnotationsDoNotMatch_NewConstraint, implementationType.FullName, serviceType.FullName);
    }
    internal static string GetTrimmingAnnotationsDoNotMatchExceptionMessage(Type implementationType, Type serviceType)
    {
        return Format(ErrorMessages.TrimmingAnnotationsDoNotMatch, implementationType.FullName, serviceType.FullName);
    }
    internal static string GetArityOfOpenGenericServiceNotEqualArityOfOpenGenericImplementationExceptionMessage(Type implementationType, Type serviceType)
    {
        return Format(ErrorMessages.ArityOfOpenGenericServiceNotEqualArityOfOpenGenericImplementation, implementationType, serviceType);
    }
    internal static string GetOpenGenericServiceRequiresOpenGenericImplementationExceptionMessage(Type serviceType)
    {
        return Format(ErrorMessages.OpenGenericServiceRequiresOpenGenericImplementation, serviceType);
    }
    internal static string GetTypeCannotBeActivatedExceptionMessage(Type implementationType, Type serviceType)
    {
        return Format(ErrorMessages.TypeCannotBeActivated, implementationType, serviceType);
    }
    internal static string GetConstantCantBeConvertedToServiceType(Type defaultType, Type serviceType)
    {
        return Format(ErrorMessages.ConstantCantBeConvertedToServiceType, defaultType, serviceType);
    }
    internal static string GetImplementationTypeCantBeConvertedToServiceType(Type implementationType, Type serviceType)
    {
        return Format(ErrorMessages.ImplementationTypeCantBeConvertedToServiceType, implementationType, serviceType);
    }
    internal static string GetCircularDependencyExceptionMessage(string typeName)
    {
        return Format(ErrorMessages.CircularDependencyException, typeName);
    }

    private static readonly bool _isUsingResourceKeys = AppContext.TryGetSwitch("System.Resources.UseSystemResourceKeys", out bool enabled) && enabled;

    private static string Format(string message, params object?[] args)
    {
        if (_isUsingResourceKeys)
        {
            return args.Length <= 3 ? string.Join(", ", message, args) : message + ", " + string.Join(", ", args);
        }

        return string.Format(CultureInfo.CurrentCulture, message, args);
    }
}
