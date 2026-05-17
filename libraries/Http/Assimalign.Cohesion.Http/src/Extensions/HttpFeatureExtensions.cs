using System;

namespace Assimalign.Cohesion.Http;

public static class HttpFeatureExtensions
{
    extension(IHttpFeatureCollection features)
    {
        public TFeature? Get<TFeature>()
        {
            Type type = typeof(TFeature);

            if (type.IsValueType)
            {
                object? obj = features.Get(type.Name);
                if (obj == null && Nullable.GetUnderlyingType(type) == null)
                {
                    throw new InvalidOperationException($"{typeof(TFeature).FullName} does not exist in the feature collection and because it is a struct the method can't return null. Use 'featureCollection[typeof({typeof(TFeature).FullName})] is not null' to check if the feature exists.");
                }
                return (TFeature?)obj;
            }
            return (TFeature?)features.Get(type.Name);
        }
    }
}
