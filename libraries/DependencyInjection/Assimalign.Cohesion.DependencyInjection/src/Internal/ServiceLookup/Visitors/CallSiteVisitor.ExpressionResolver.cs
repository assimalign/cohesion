using System;
using System.Linq;
using System.Linq.Expressions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;


namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class CallSiteExpressionResolverBuilderVisitor : CallSiteVisitor<object?, Expression>
{
    private static readonly ParameterExpression _scopeParameter = Expression.Parameter(typeof(ServiceProviderEngineScope));

    private static readonly ParameterExpression _resolvedServices = Expression.Variable(typeof(IDictionary<CallSiteServiceCacheKey, object>), _scopeParameter.Name + "resolvedServices");
    private static readonly ParameterExpression _sync = Expression.Variable(typeof(object), _scopeParameter.Name + "sync");
    private static readonly BinaryExpression _resolvedServicesVariableAssignment =
        Expression.Assign(_resolvedServices,
            Expression.Property(
                _scopeParameter,
                typeof(ServiceProviderEngineScope).GetProperty(nameof(ServiceProviderEngineScope.ResolvedServices), BindingFlags.Instance | BindingFlags.NonPublic)!));

    private static readonly BinaryExpression _syncVariableAssignment =
        Expression.Assign(_sync,
            Expression.Property(
                _scopeParameter,
                typeof(ServiceProviderEngineScope).GetProperty(nameof(ServiceProviderEngineScope.Sync), BindingFlags.Instance | BindingFlags.NonPublic)!));

    private static readonly ParameterExpression _captureDisposableParameter = Expression.Parameter(typeof(object));
    private static readonly LambdaExpression _captureDisposable = Expression.Lambda(
                Expression.Call(_scopeParameter, ServiceLookupHelpers.CaptureDisposableMethodInfo, _captureDisposableParameter),
                _captureDisposableParameter);

    private static readonly ConstantExpression _callSiteRuntimeResolverInstanceExpression = Expression.Constant(
        CallSiteRuntimeResolverVisitor.Instance,
        typeof(CallSiteRuntimeResolverVisitor));

    private readonly ServiceProviderEngineScope _rootScope;

    private readonly ConcurrentDictionary<CallSiteServiceCacheKey, Func<ServiceProviderEngineScope, object>> _scopeResolverCache;

    private readonly Func<CallSiteServiceCacheKey, CallSiteService, Func<ServiceProviderEngineScope, object>> _buildTypeDelegate;

    public CallSiteExpressionResolverBuilderVisitor(ServiceProvider serviceProvider)
    {
        _rootScope = serviceProvider.Root;
        _scopeResolverCache = new ConcurrentDictionary<CallSiteServiceCacheKey, Func<ServiceProviderEngineScope, object>>();
        _buildTypeDelegate = (key, cs) => BuildNoCache(cs);
    }

    public Func<ServiceProviderEngineScope, object> Build(CallSiteService callSite)
    {
        // Only scope methods are cached
        if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
        {
            return _scopeResolverCache.GetOrAdd(callSite.Cache.Key, _buildTypeDelegate, callSite);

        }

        return BuildNoCache(callSite);
    }

    public Func<ServiceProviderEngineScope, object> BuildNoCache(CallSiteService callSite)
    {
        Expression<Func<ServiceProviderEngineScope, object>> expression = BuildExpression(callSite);
        ServiceEventSource.Log.ExpressionTreeGenerated(_rootScope.RootProvider, callSite.ServiceType, expression);
        return expression.Compile();
    }

    private Expression<Func<ServiceProviderEngineScope, object>> BuildExpression(CallSiteService callSite)
    {
        if (callSite.Cache.Location == CallSiteResultCacheLocation.Scope)
        {
            return Expression.Lambda<Func<ServiceProviderEngineScope, object>>(
                Expression.Block(
                    new[] { _resolvedServices, _sync },
                    _resolvedServicesVariableAssignment,
                    _syncVariableAssignment,
                    BuildScopedExpression(callSite)),
                _scopeParameter);
        }

        return Expression.Lambda<Func<ServiceProviderEngineScope, object>>(
            Convert(VisitCallSite(callSite, null), typeof(object), forceValueTypeConversion: true),
            _scopeParameter);
    }

    protected override Expression VisitRootCache(CallSiteService singletonCallSite, object? context)
    {
        return Expression.Constant(CallSiteRuntimeResolverVisitor.Instance.Resolve(singletonCallSite, _rootScope));
    }

    protected override Expression VisitConstant(ConstantCallSite constantCallSite, object? context)
    {
        return Expression.Constant(constantCallSite.DefaultValue);
    }

    protected override Expression VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, object? context)
    {
        return _scopeParameter;
    }

    protected override Expression VisitFactory(FactoryCallSite factoryCallSite, object? context)
    {
        return Expression.Invoke(Expression.Constant(factoryCallSite.Factory), _scopeParameter);
    }

    protected override Expression VisitEnumerable(EnumerableCallSite callSite, object? context)
    {
        [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
            Justification = "VerifyAotCompatibility ensures elementType is not a ValueType")]
        static MethodInfo GetArrayEmptyMethodInfo(Type elementType)
        {
            Debug.Assert(!RuntimeFeature.IsDynamicCodeSupported || !elementType.IsValueType, "VerifyAotCompatibility=true will throw during building the IEnumerableCallSite if elementType is a ValueType.");

            return ServiceLookupHelpers.GetArrayEmptyMethodInfo(elementType);
        }

        if (callSite.ServiceCallSites.Length == 0)
        {
            return Expression.Constant(
                GetArrayEmptyMethodInfo(callSite.ItemType)
                .Invoke(obj: null, parameters: Array.Empty<object>()));
        }

        return Expression.NewArrayInit(
            callSite.ItemType,
            callSite.ServiceCallSites.Select(cs =>
                Convert(
                    VisitCallSite(cs, context),
                    callSite.ItemType)));
    }

    protected override Expression VisitDisposeCache(CallSiteService callSite, object? context)
    {
        // Elide calls to GetCaptureDisposable if the implementation type isn't disposable
        return TryCaptureDisposable(
            callSite,
            _scopeParameter,
            VisitCallSiteMain(callSite, context));
    }

    private static Expression TryCaptureDisposable(CallSiteService callSite, ParameterExpression scope, Expression service)
    {
        if (!callSite.CaptureDisposable)
        {
            return service;
        }

        return Expression.Invoke(GetCaptureDisposable(scope), service);
    }

    protected override Expression VisitConstructor(ConstructorCallSite callSite, object? context)
    {
        ParameterInfo[] parameters = callSite.ConstructorInfo.GetParameters();
        Expression[] parameterExpressions;
        if (callSite.ParameterCallSites.Length == 0)
        {
            parameterExpressions = Array.Empty<Expression>();
        }
        else
        {
            parameterExpressions = new Expression[callSite.ParameterCallSites.Length];
            for (int i = 0; i < parameterExpressions.Length; i++)
            {
                parameterExpressions[i] = Convert(VisitCallSite(callSite.ParameterCallSites[i], context), parameters[i].ParameterType);
            }
        }

        Expression expression = Expression.New(callSite.ConstructorInfo, parameterExpressions);
        if (callSite.ImplementationType!.IsValueType)
        {
            expression = Expression.Convert(expression, typeof(object));
        }
        return expression;
    }

    private static Expression Convert(Expression expression, Type type, bool forceValueTypeConversion = false)
    {
        // Don't convert if the expression is already assignable
        if (type.IsAssignableFrom(expression.Type)
            && (!expression.Type.IsValueType || !forceValueTypeConversion))
        {
            return expression;
        }

        return Expression.Convert(expression, type);
    }

    protected override Expression VisitScopeCache(CallSiteService callSite, object? context)
    {
        Func<ServiceProviderEngineScope, object> lambda = Build(callSite);
        return Expression.Invoke(Expression.Constant(lambda), _scopeParameter);
    }

    // Move off the main stack
    private ConditionalExpression BuildScopedExpression(CallSiteService callSite)
    {
        ConstantExpression callSiteExpression = Expression.Constant(
            callSite,
            typeof(CallSiteService));

        // We want to directly use the callsite value if it's set and the scope is the root scope.
        // We've already called into the RuntimeResolver and pre-computed any singletons or root scope
        // Avoid the compilation for singletons (or promoted singletons)
        MethodCallExpression resolveRootScopeExpression = Expression.Call(
            _callSiteRuntimeResolverInstanceExpression,
            ServiceLookupHelpers.ResolveCallSiteAndScopeMethodInfo,
            callSiteExpression,
            _scopeParameter);

        ConstantExpression keyExpression = Expression.Constant(
            callSite.Cache.Key,
            typeof(CallSiteServiceCacheKey));

        ParameterExpression resolvedVariable = Expression.Variable(typeof(object), "resolved");

        ParameterExpression resolvedServices = _resolvedServices;

        MethodCallExpression tryGetValueExpression = Expression.Call(
            resolvedServices,
            ServiceLookupHelpers.TryGetValueMethodInfo,
            keyExpression,
            resolvedVariable);

        Expression captureDisposible = TryCaptureDisposable(callSite, _scopeParameter, VisitCallSiteMain(callSite, null));

        BinaryExpression assignExpression = Expression.Assign(
            resolvedVariable,
            captureDisposible);

        MethodCallExpression addValueExpression = Expression.Call(
            resolvedServices,
            ServiceLookupHelpers.AddMethodInfo,
            keyExpression,
            resolvedVariable);

        BlockExpression blockExpression = Expression.Block(
            typeof(object),
            new[]
            {
                    resolvedVariable
            },
            Expression.IfThen(
                Expression.Not(tryGetValueExpression),
                Expression.Block(
                    assignExpression,
                    addValueExpression)),
            resolvedVariable);


        // The C# compiler would copy the lock object to guard against mutation.
        // We don't, since we know the lock object is readonly.
        ParameterExpression lockWasTaken = Expression.Variable(typeof(bool), "lockWasTaken");
        ParameterExpression sync = _sync;

        MethodCallExpression monitorEnter = Expression.Call(ServiceLookupHelpers.MonitorEnterMethodInfo, sync, lockWasTaken);
        MethodCallExpression monitorExit = Expression.Call(ServiceLookupHelpers.MonitorExitMethodInfo, sync);

        BlockExpression tryBody = Expression.Block(monitorEnter, blockExpression);
        ConditionalExpression finallyBody = Expression.IfThen(lockWasTaken, monitorExit);

        return Expression.Condition(
                Expression.Property(
                    _scopeParameter,
                    typeof(ServiceProviderEngineScope)
                        .GetProperty(nameof(ServiceProviderEngineScope.IsRootScope), BindingFlags.Instance | BindingFlags.Public)!),
                resolveRootScopeExpression,
                Expression.Block(
                    typeof(object),
                    new[] { lockWasTaken },
                    Expression.TryFinally(tryBody, finallyBody))
            );
    }

    public static Expression GetCaptureDisposable(ParameterExpression scope)
    {
        if (scope != _scopeParameter)
        {
            throw new NotSupportedException(Resources.GetCaptureDisposableNotSupported);
        }
        return _captureDisposable;
    }
}