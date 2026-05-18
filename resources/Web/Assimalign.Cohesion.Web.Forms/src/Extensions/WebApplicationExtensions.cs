namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        public IWebApplicationPipelineBuilder UseForms()
        {
            return builder.Use(async (context, next) =>
            {
                // Get the request from the context
                IHttpRequest request = context.Request;

                // Get the feature 
                IHttpFeatureCollection features = context.Features;
                IHttpFormFeature? feature = features.Get<IHttpFormFeature>();

                if (feature is null)
                {
                    feature = new HttpFormFeature(request);
                    features.Set<IHttpFormFeature>(feature);
                }

                await feature.ReadFormAsync();
                await next.Invoke(context);
            });
        }
    }
}
