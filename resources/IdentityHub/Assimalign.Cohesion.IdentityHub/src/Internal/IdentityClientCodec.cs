using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// Stores app registrations in the <c>clients</c> record set: the registration's identifier is
/// the record's key, each audience it exposes is a unique <c>audience</c> index entry, and the
/// rest is a JSON payload laid out by <see cref="IdentityClientPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// The unique index is what makes "no two registrations expose one audience" hold when two
/// writers race: the store refuses the second write.
/// </para>
/// <para>
/// A record is decoded by rebuilding a descriptor and creating the registration from it, so a
/// stored registration passes every check a new one does. A secret is stored as its verifier,
/// never in the clear.
/// </para>
/// </remarks>
internal sealed class IdentityClientCodec : IIdentityHubRecordCodec<IdentityClient>
{
    private const int layout = 1;

    private const string authorizationCode = "authorization_code";
    private const string clientCredentials = "client_credentials";
    private const string deviceCode = "urn:ietf:params:oauth:grant-type:device_code";
    private const string refreshToken = "refresh_token";

    private const string web = "web";
    private const string singlePage = "spa";
    private const string native = "native";

    private const string secret = "secret";
    private const string certificate = "certificate";
    private const string federated = "federated";

    private const string delegated = "delegated";
    private const string application = "application";
    private const string user = "user";

    // The payload is never placed in markup, so only what JSON itself requires is escaped. That
    // keeps a registration at its limits well inside the store's payload limit.
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private IdentityClientCodec()
    {
    }

    public static IdentityClientCodec Instance { get; } = new();

    public static IdentityHubRecordIndexName AudienceIndex { get; } = new("audience");

    public IdentityHubRecordSetName Set { get; } = new("clients");

    public IdentityHubRecord Encode(IdentityClient value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var payload = new IdentityClientPayload
        {
            Layout = layout,
            DisplayName = value.DisplayName,
            Description = value.Description,
            Grants = WriteGrants(value.Grants),
            AccessTokenLifetime = value.AccessTokenLifetime,
            Redirects = WriteRedirects(value.Redirects),
            Credentials = WriteCredentials(value.Credentials),
            Audiences = WriteAudiences(value.Audiences),
            Scopes = WriteScopes(value.Scopes),
            Roles = WriteRoles(value.Roles),
            Permissions = WritePermissions(value.Permissions),
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            JsonSerializer.Serialize(writer, payload, IdentityClientPayloadContext.Default.IdentityClientPayload);
        }

        var indexes = new IdentityHubRecordIndex[value.Audiences.Count];
        for (int index = 0; index < indexes.Length; index++)
        {
            indexes[index] = new IdentityHubRecordIndex(
                AudienceIndex,
                new IdentityHubRecordKey(value.Audiences[index].Value),
                isUnique: true);
        }

        return new IdentityHubRecord(
            new IdentityHubRecordKey(value.Id.Value),
            buffer.WrittenMemory,
            expiresAt: null,
            indexes);
    }

    public IdentityClient Decode(IdentityHubRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        IdentityClientPayload payload =
            JsonSerializer.Deserialize(record.Payload.Span, IdentityClientPayloadContext.Default.IdentityClientPayload)
            ?? throw new FormatException(ErrorMessages.ClientPayloadInvalid);

        if (payload.Layout is not layout)
        {
            throw new FormatException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientLayoutUnknown, payload.Layout));
        }

        var descriptor = new IdentityClientDescriptor
        {
            Id = new IdentityClientId(record.Key.Value),
            DisplayName = Require(payload.DisplayName),
            Description = payload.Description,
            Grants = ReadGrants(payload.Grants),
            AccessTokenLifetime = payload.AccessTokenLifetime,
        };

        if (payload.Redirects is not null)
        {
            foreach (IdentityClientPayload.Redirect? redirect in payload.Redirects)
            {
                IdentityClientPayload.Redirect stored = Require(redirect);
                descriptor.Redirects.Add(new IdentityClientRedirect(Require(stored.Uri), ReadPlatform(stored.Platform)));
            }
        }

        if (payload.Credentials is not null)
        {
            foreach (IdentityClientPayload.Credential? credential in payload.Credentials)
            {
                descriptor.Credentials.Add(ReadCredential(Require(credential)));
            }
        }

        if (payload.Audiences is not null)
        {
            foreach (string? audience in payload.Audiences)
            {
                descriptor.Audiences.Add(new IdentityClientAudience(Require(audience)));
            }
        }

        if (payload.Scopes is not null)
        {
            foreach (IdentityClientPayload.Scope? scope in payload.Scopes)
            {
                IdentityClientPayload.Scope stored = Require(scope);
                descriptor.Scopes.Add(IdentityClientScope.Create(
                    new IdentityClientPermissionName(Require(stored.Name)),
                    Require(stored.DisplayName),
                    stored.Description));
            }
        }

        if (payload.Roles is not null)
        {
            foreach (IdentityClientPayload.Role? role in payload.Roles)
            {
                IdentityClientPayload.Role stored = Require(role);
                descriptor.Roles.Add(IdentityClientRole.Create(
                    new IdentityClientPermissionName(Require(stored.Name)),
                    ReadMembers(stored.Members),
                    Require(stored.DisplayName),
                    stored.Description));
            }
        }

        if (payload.Permissions is not null)
        {
            foreach (IdentityClientPayload.Permission? permission in payload.Permissions)
            {
                IdentityClientPayload.Permission stored = Require(permission);
                descriptor.Permissions.Add(new IdentityClientPermission(
                    new IdentityClientId(Require(stored.Resource)),
                    new IdentityClientPermissionName(Require(stored.Name)),
                    ReadPermissionKind(stored.Kind)));
            }
        }

        return IdentityClient.Create(descriptor);
    }

    private static List<string>? WriteGrants(IdentityClientGrants grants)
    {
        if (grants is IdentityClientGrants.None)
        {
            return null;
        }

        var names = new List<string>(4);
        if ((grants & IdentityClientGrants.AuthorizationCode) is not IdentityClientGrants.None)
        {
            names.Add(authorizationCode);
        }

        if ((grants & IdentityClientGrants.ClientCredentials) is not IdentityClientGrants.None)
        {
            names.Add(clientCredentials);
        }

        if ((grants & IdentityClientGrants.DeviceCode) is not IdentityClientGrants.None)
        {
            names.Add(deviceCode);
        }

        if ((grants & IdentityClientGrants.RefreshToken) is not IdentityClientGrants.None)
        {
            names.Add(refreshToken);
        }

        return names;
    }

    private static IdentityClientGrants ReadGrants(List<string>? names)
    {
        var grants = IdentityClientGrants.None;
        if (names is null)
        {
            return grants;
        }

        foreach (string? name in names)
        {
            grants |= name switch
            {
                authorizationCode => IdentityClientGrants.AuthorizationCode,
                clientCredentials => IdentityClientGrants.ClientCredentials,
                deviceCode => IdentityClientGrants.DeviceCode,
                refreshToken => IdentityClientGrants.RefreshToken,
                _ => throw new FormatException(ErrorMessages.ClientPayloadInvalid),
            };
        }

        return grants;
    }

    private static List<IdentityClientPayload.Redirect>? WriteRedirects(IReadOnlyList<IdentityClientRedirect> redirects)
    {
        if (redirects.Count is 0)
        {
            return null;
        }

        var stored = new List<IdentityClientPayload.Redirect>(redirects.Count);
        for (int index = 0; index < redirects.Count; index++)
        {
            IdentityClientRedirect redirect = redirects[index];
            stored.Add(new IdentityClientPayload.Redirect
            {
                Uri = redirect.Uri,
                Platform = redirect.Platform switch
                {
                    IdentityClientPlatform.Web => web,
                    IdentityClientPlatform.SinglePage => singlePage,
                    IdentityClientPlatform.Native => native,
                    _ => throw new UnreachableException(),
                },
            });
        }

        return stored;
    }

    private static IdentityClientPlatform ReadPlatform(string? name)
    {
        return name switch
        {
            web => IdentityClientPlatform.Web,
            singlePage => IdentityClientPlatform.SinglePage,
            native => IdentityClientPlatform.Native,
            _ => throw new FormatException(ErrorMessages.ClientPayloadInvalid),
        };
    }

    private static List<IdentityClientPayload.Credential>? WriteCredentials(IReadOnlyList<IdentityClientCredential> credentials)
    {
        if (credentials.Count is 0)
        {
            return null;
        }

        var stored = new List<IdentityClientPayload.Credential>(credentials.Count);
        for (int index = 0; index < credentials.Count; index++)
        {
            IdentityClientCredential credential = credentials[index];
            var entry = new IdentityClientPayload.Credential
            {
                Id = credential.Id.Value,
                DisplayName = credential.DisplayName,
                NotBefore = credential.NotBefore,
                ExpiresAt = credential.ExpiresAt,
            };

            switch (credential)
            {
                case IdentityClientSecretCredential known:
                    entry.Kind = secret;
                    entry.Salt = known.Salt.ToArray();
                    entry.Verifier = known.Verifier.ToArray();
                    entry.Iterations = known.Iterations;
                    break;

                case IdentityClientCertificateCredential known:
                    entry.Kind = certificate;
                    entry.Certificate = known.Certificate.ToArray();
                    break;

                case IdentityClientFederatedCredential known:
                    entry.Kind = federated;
                    entry.Issuer = known.Issuer;
                    entry.Subject = known.Subject;
                    entry.Audiences = WriteAudiences(known.Audiences);
                    break;

                default:
                    // The credential family is closed: its base constructor is private protected.
                    throw new UnreachableException();
            }

            stored.Add(entry);
        }

        return stored;
    }

    private static IdentityClientCredential ReadCredential(IdentityClientPayload.Credential stored)
    {
        var id = new IdentityClientCredentialId(Require(stored.Id));
        switch (stored.Kind)
        {
            case secret:
                if (stored.Certificate is not null || stored.Issuer is not null ||
                    stored.Subject is not null || stored.Audiences is not null)
                {
                    break;
                }

                return IdentityClientSecretCredential.Restore(
                    id,
                    stored.DisplayName,
                    stored.NotBefore,
                    stored.ExpiresAt,
                    stored.Salt,
                    stored.Verifier,
                    stored.Iterations ?? 0);

            case certificate:
                if (stored.Salt is not null || stored.Verifier is not null || stored.Iterations is not null ||
                    stored.Issuer is not null || stored.Subject is not null || stored.Audiences is not null)
                {
                    break;
                }

                return IdentityClientCertificateCredential.Restore(
                    id,
                    stored.DisplayName,
                    stored.NotBefore,
                    stored.ExpiresAt,
                    stored.Certificate);

            case federated:
                if (stored.Salt is not null || stored.Verifier is not null || stored.Iterations is not null ||
                    stored.Certificate is not null)
                {
                    break;
                }

                var audiences = new List<IdentityClientAudience>();
                foreach (string? audience in Require(stored.Audiences))
                {
                    audiences.Add(new IdentityClientAudience(Require(audience)));
                }

                return IdentityClientFederatedCredential.Create(
                    id,
                    Require(stored.Issuer),
                    Require(stored.Subject),
                    audiences,
                    stored.DisplayName,
                    stored.NotBefore,
                    stored.ExpiresAt);
        }

        throw new FormatException(ErrorMessages.ClientPayloadInvalid);
    }

    private static List<string>? WriteAudiences(IReadOnlyList<IdentityClientAudience> audiences)
    {
        if (audiences.Count is 0)
        {
            return null;
        }

        var stored = new List<string>(audiences.Count);
        for (int index = 0; index < audiences.Count; index++)
        {
            stored.Add(audiences[index].Value);
        }

        return stored;
    }

    private static List<IdentityClientPayload.Scope>? WriteScopes(IReadOnlyList<IdentityClientScope> scopes)
    {
        if (scopes.Count is 0)
        {
            return null;
        }

        var stored = new List<IdentityClientPayload.Scope>(scopes.Count);
        for (int index = 0; index < scopes.Count; index++)
        {
            IdentityClientScope scope = scopes[index];
            stored.Add(new IdentityClientPayload.Scope
            {
                Name = scope.Name.Value,
                DisplayName = scope.DisplayName,
                Description = scope.Description,
            });
        }

        return stored;
    }

    private static List<IdentityClientPayload.Role>? WriteRoles(IReadOnlyList<IdentityClientRole> roles)
    {
        if (roles.Count is 0)
        {
            return null;
        }

        var stored = new List<IdentityClientPayload.Role>(roles.Count);
        for (int index = 0; index < roles.Count; index++)
        {
            IdentityClientRole role = roles[index];
            var members = new List<string>(2);
            if ((role.Members & IdentityClientRoleMembers.Users) is not IdentityClientRoleMembers.None)
            {
                members.Add(user);
            }

            if ((role.Members & IdentityClientRoleMembers.Applications) is not IdentityClientRoleMembers.None)
            {
                members.Add(application);
            }

            stored.Add(new IdentityClientPayload.Role
            {
                Name = role.Name.Value,
                Members = members,
                DisplayName = role.DisplayName,
                Description = role.Description,
            });
        }

        return stored;
    }

    private static IdentityClientRoleMembers ReadMembers(List<string>? names)
    {
        var members = IdentityClientRoleMembers.None;
        foreach (string? name in Require(names))
        {
            members |= name switch
            {
                user => IdentityClientRoleMembers.Users,
                application => IdentityClientRoleMembers.Applications,
                _ => throw new FormatException(ErrorMessages.ClientPayloadInvalid),
            };
        }

        return members;
    }

    private static List<IdentityClientPayload.Permission>? WritePermissions(IReadOnlyList<IdentityClientPermission> permissions)
    {
        if (permissions.Count is 0)
        {
            return null;
        }

        var stored = new List<IdentityClientPayload.Permission>(permissions.Count);
        for (int index = 0; index < permissions.Count; index++)
        {
            IdentityClientPermission permission = permissions[index];
            stored.Add(new IdentityClientPayload.Permission
            {
                Resource = permission.Resource.Value,
                Name = permission.Name.Value,
                Kind = permission.Kind switch
                {
                    IdentityClientPermissionKind.Delegated => delegated,
                    IdentityClientPermissionKind.Application => application,
                    _ => throw new UnreachableException(),
                },
            });
        }

        return stored;
    }

    private static IdentityClientPermissionKind ReadPermissionKind(string? name)
    {
        return name switch
        {
            delegated => IdentityClientPermissionKind.Delegated,
            application => IdentityClientPermissionKind.Application,
            _ => throw new FormatException(ErrorMessages.ClientPayloadInvalid),
        };
    }

    private static T Require<T>(T? value)
        where T : class
    {
        return value ?? throw new FormatException(ErrorMessages.ClientPayloadInvalid);
    }
}
