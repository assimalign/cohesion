using System;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Caching.Internal;

internal sealed class OutputCacheFieldNameCollection : Collection<HttpFieldName>
{
    protected override void InsertItem(int index, HttpFieldName item)
    {
        Validate(item);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, HttpFieldName item)
    {
        Validate(item);
        base.SetItem(index, item);
    }

    private static void Validate(HttpFieldName item)
    {
        if (item.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CacheFieldNameInvalid, nameof(item));
        }
    }
}
