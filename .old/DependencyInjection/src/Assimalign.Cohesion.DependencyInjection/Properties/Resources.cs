using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Properties
{
    internal partial class Resources
    {
        internal static string GetTryAddIndistinguishableTypeToEnumerableExceptionMessage(Type p1, Type p2)
        {
            return Format(TryAddIndistinguishableTypeToEnumerable, p1, p2);
        }
        internal static string GetCallSiteTypeNotSupportedExceptionMessage(Type p1)
        {
            return Format(CallSiteTypeNotSupported, p1);
        }
        internal static string GetNoServiceRegisteredExceptionMessage(Type p1)
        {
            return Format(NoServiceRegistered, p1);
        }
        internal static string GetAsyncDisposableServiceDisposeExceptionMessage(string p1)
        {
            return Format(AsyncDisposableServiceDispose, p1);
        }
        internal static string GetDirectScopedResolvedFromRootExceptionMessage(Type t1, string t2)
        {
            return Format(DirectScopedResolvedFromRootException, t1, t2);
        }
        internal static string GetScopedResolvedFromRootExceptionMessage(Type t1, Type t2, string t3)
        {
            return Format(ScopedResolvedFromRootException, t1, t2, t3);
        }
        internal static string GetScopedInSingletonExceptionMessage(Type t1, Type t2, string t3, string t4)
        {
            return Format(ScopedInSingletonException, t1, t2, t3, t4);
        }
        internal static string GetCannotResolveServiceExceptionMessage(Type parameterType, Type implementationType)
        {
            return Format(CannotResolveService, parameterType, implementationType);
        }
        internal static string GetUnableToActivateTypeExceptionMessage(Type implementationType)
        {
            return Format(UnableToActivateTypeException, implementationType);
        }
        internal static string GetAmbiguousConstructorExceptionMessage(Type implementationType)
        {
            return Format(AmbiguousConstructorException, implementationType);
        }
        internal static string GetNoConstructorMatchExceptionMessage(Type implementationType)
        {
            return Format(NoConstructorMatch, implementationType);
        }
        internal static string GetTrimmingAnnotationsDoNotMatch_NewConstraintExceptionMessage(Type implementationType, Type serviceType)
        {
            return Format(TrimmingAnnotationsDoNotMatch_NewConstraint, implementationType.FullName, serviceType.FullName);
        }
        internal static string GetTrimmingAnnotationsDoNotMatchExceptionMessage(Type implementationType, Type serviceType)
        {
            return Format(TrimmingAnnotationsDoNotMatch, implementationType.FullName, serviceType.FullName);
        }
        internal static string GetArityOfOpenGenericServiceNotEqualArityOfOpenGenericImplementationExceptionMessage(Type implementationType, Type serviceType)
        {
            return Format(ArityOfOpenGenericServiceNotEqualArityOfOpenGenericImplementation, implementationType, serviceType);
        }
        internal static string GetOpenGenericServiceRequiresOpenGenericImplementationExceptionMessage(Type serviceType)
        {
            return Format(OpenGenericServiceRequiresOpenGenericImplementation, serviceType);
        }
        internal static string GetTypeCannotBeActivatedExceptionMessage(Type implementationType, Type serviceType)
        {
            return Format(TypeCannotBeActivated, implementationType, serviceType);
        }
        internal static string GetConstantCantBeConvertedToServiceType(Type defaultType, Type serviceType)
        {
            return Format(ConstantCantBeConvertedToServiceType, defaultType, serviceType);
        }
        internal static string GetImplementationTypeCantBeConvertedToServiceType(Type implementationType, Type serviceType)
        {
            return Format(ImplementationTypeCantBeConvertedToServiceType, implementationType, serviceType);
        }
        internal static string GetCircularDependencyExceptionMessage(string typeName)
        {
            return Format(CircularDependencyException, typeName);
        }
    }
}
