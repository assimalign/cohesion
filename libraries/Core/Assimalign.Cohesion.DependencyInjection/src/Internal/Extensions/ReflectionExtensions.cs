using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal static class ReflectionExtensions
{
    internal static bool CheckHasDefaultValue(this ParameterInfo parameter, out bool tryToGetDefaultValue)
    {
        tryToGetDefaultValue = true;
        return parameter.HasDefaultValue;
    }

    internal static bool TryGetDefaultValue(this ParameterInfo parameter, out object? defaultValue)
    {
        bool hasDefaultValue = parameter.CheckHasDefaultValue( out bool tryToGetDefaultValue);
        defaultValue = null;

        if (hasDefaultValue)
        {
            if (tryToGetDefaultValue)
            {
                defaultValue = parameter.DefaultValue;
            }

            bool isNullableParameterType = parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(Nullable<>);

            // Workaround for https://github.com/dotnet/runtime/issues/18599
            if (defaultValue == null && parameter.ParameterType.IsValueType && !isNullableParameterType) // Nullable types should be left null
            {
                defaultValue = CreateValueType(parameter.ParameterType);
            }

            [UnconditionalSuppressMessage(
                "ReflectionAnalysis",
                "IL2067:UnrecognizedReflectionPattern",
                Justification = "CreateValueType is only called on a ValueType. You can always create an instance of a ValueType.")]
            static object? CreateValueType(Type t) => RuntimeHelpers.GetUninitializedObject(t);


            // Handle nullable enums
            if (defaultValue != null && isNullableParameterType)
            {
                Type? underlyingType = Nullable.GetUnderlyingType(parameter.ParameterType);
                if (underlyingType != null && underlyingType.IsEnum)
                {
                    defaultValue = Enum.ToObject(underlyingType, defaultValue);
                }
            }
        }

        return hasDefaultValue;
    }
}
