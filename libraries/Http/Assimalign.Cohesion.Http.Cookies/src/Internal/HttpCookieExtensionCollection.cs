using System;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.Http.Internal;

// A default value object bypasses construction. Guard both mutation paths so
// the public IList never holds an empty attribute, including after a copy.
internal sealed class HttpCookieExtensionCollection : Collection<HttpCookieExtension>
{
    protected override void InsertItem(int index, HttpCookieExtension item)
    {
        if (item.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CookieExtensionInvalid, nameof(item));
        }

        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, HttpCookieExtension item)
    {
        if (item.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CookieExtensionInvalid, nameof(item));
        }

        base.SetItem(index, item);
    }
}
