namespace Assimalign.Cohesion.Web.Caching.Internal;

internal enum OutputCacheSkipReason
{
    BodyOverCap,
    StatusNotCacheable,
    SetCookie,
    CacheControlForbids,
    VaryStar,
    NoFreshness,
    DecidedBeforeRouting,
}
