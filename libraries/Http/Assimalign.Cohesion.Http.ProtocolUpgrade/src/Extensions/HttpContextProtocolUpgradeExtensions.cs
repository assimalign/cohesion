using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http.Extensions
{
    public static class HttpContextProtocolUpgradeExtensions
    {
        extension(IHttpRequest request)
        {
            /// <summary>
            /// Gets the protocol-upgrade feature for this exchange, or <see langword="null"/>
            /// when the exchange is not a candidate for a connection transition.
            /// </summary>
            /// <remarks>
            /// <para>
            /// A non-null value indicates that the request matches either the RFC 9110 §7.8
            /// upgrade signal (<c>Connection: upgrade</c> + <c>Upgrade</c>) or the
            /// RFC 9110 §9.3.6 <c>CONNECT</c> tunnel shape. Inspect
            /// <see cref="IHttpProtocolUpgrade.Kind"/> to disambiguate.
            /// </para>
            /// <para>
            /// Most exchanges are normal request/response and this property returns
            /// <see langword="null"/>.
            /// </para>
            /// </remarks>
            public IHttpProtocolUpgrade? Upgrade
            {
                get
                {

                }
            }
        }
        
    }
}
