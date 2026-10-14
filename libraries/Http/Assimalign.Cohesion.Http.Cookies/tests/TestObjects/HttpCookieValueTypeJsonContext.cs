using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

// These types belong to the test assembly; HttpCookieName belongs to the referenced library.
[JsonSerializable(typeof(HttpCookieNameModel))]
[JsonSerializable(typeof(Dictionary<HttpCookieName, int>))]
internal partial class HttpCookieValueTypeJsonContext : JsonSerializerContext
{
}
