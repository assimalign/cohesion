namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The formats the library validates when <c>format</c> asserts: every format 2020-12 Validation §7.3
/// defines. Each built-in format constrains strings only.
/// </summary>
internal static class BuiltInFormats
{
    private static readonly BuiltInFormat _dateTime = new("date-time", DateTimeFormat.IsDateTime);
    private static readonly BuiltInFormat _date = new("date", DateTimeFormat.IsDate);
    private static readonly BuiltInFormat _time = new("time", DateTimeFormat.IsTime);
    private static readonly BuiltInFormat _duration = new("duration", DurationFormat.IsDuration);
    private static readonly BuiltInFormat _email = new("email", EmailFormat.IsEmail);
    private static readonly BuiltInFormat _idnEmail = new("idn-email", EmailFormat.IsIdnEmail);
    private static readonly BuiltInFormat _hostname = new("hostname", HostnameFormat.IsHostname);
    private static readonly BuiltInFormat _idnHostname = new("idn-hostname", HostnameFormat.IsIdnHostname);
    private static readonly BuiltInFormat _ipv4 = new("ipv4", IPAddressFormat.IsIPv4);
    private static readonly BuiltInFormat _ipv6 = new("ipv6", IPAddressFormat.IsIPv6);
    private static readonly BuiltInFormat _uri = new("uri", UriFormat.IsUri);
    private static readonly BuiltInFormat _uriReference = new("uri-reference", UriFormat.IsUriReference);
    private static readonly BuiltInFormat _iri = new("iri", UriFormat.IsIri);
    private static readonly BuiltInFormat _iriReference = new("iri-reference", UriFormat.IsIriReference);
    private static readonly BuiltInFormat _uriTemplate = new("uri-template", UriTemplateFormat.IsUriTemplate);
    private static readonly BuiltInFormat _jsonPointer = new("json-pointer", JsonPointerFormat.IsJsonPointer);
    private static readonly BuiltInFormat _relativeJsonPointer = new("relative-json-pointer", JsonPointerFormat.IsRelativeJsonPointer);
    private static readonly BuiltInFormat _regex = new("regex", RegexFormat.IsRegex);
    private static readonly BuiltInFormat _uuid = new("uuid", UuidFormat.IsUuid);

    /// <summary>Finds a built-in format by its exact name.</summary>
    /// <returns>The format, or <see langword="null"/> when the library defines none by that name.</returns>
    public static BuiltInFormat? Find(string name) => name switch
    {
        "date-time" => _dateTime,
        "date" => _date,
        "time" => _time,
        "duration" => _duration,
        "email" => _email,
        "idn-email" => _idnEmail,
        "hostname" => _hostname,
        "idn-hostname" => _idnHostname,
        "ipv4" => _ipv4,
        "ipv6" => _ipv6,
        "uri" => _uri,
        "uri-reference" => _uriReference,
        "iri" => _iri,
        "iri-reference" => _iriReference,
        "uri-template" => _uriTemplate,
        "json-pointer" => _jsonPointer,
        "relative-json-pointer" => _relativeJsonPointer,
        "regex" => _regex,
        "uuid" => _uuid,
        _ => null,
    };
}
