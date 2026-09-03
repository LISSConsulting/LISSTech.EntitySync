using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.OrchestraMSP;

public sealed class OrchestraEntityAdapter :
    IEntityAdapter,
    ICanonicalEntityVersionAdapter,
    IEntityWriteParentResolver,
    IDisposable
{
    private static readonly IReadOnlyList<string> LookupTypeValues = ["PlatformLink"];
    private readonly OrchestraClientDirectoryClient client;
    private int disposed;

    public OrchestraEntityAdapter(OrchestraClientDirectoryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Vendor => EntitySyncVendors.OrchestraMSP;
    public IReadOnlyList<string> LookupTypes => LookupTypeValues;

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(query);
        var clients = await client.ListClientsAsync(query.IncludeInactive, cancellationToken).ConfigureAwait(false);
        IEnumerable<ExternalEntity> entities = query.EntityType.Trim() switch
        {
            var type when type.Equals("Client", StringComparison.OrdinalIgnoreCase) => clients.Select(OrchestraEntityMapper.MapClient),
            var type when type.Equals("Site", StringComparison.OrdinalIgnoreCase) => clients.SelectMany(value => value.Sites).Select(OrchestraEntityMapper.MapSite),
            var type when type.Equals("Address", StringComparison.OrdinalIgnoreCase) =>
                MapUniqueAddresses(clients),
            _ => throw new NotSupportedException("OrchestraMSP supports Client, Site, and Address entities.")
        };
        if (!query.IncludeInactive) entities = entities.Where(entity => entity.IsActive == true);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            entities = entities.Where(entity => entity.Id.Equals(search, StringComparison.OrdinalIgnoreCase) || entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Count is <= 0) throw new ArgumentOutOfRangeException(nameof(query), "Entity count must be positive when supplied.");
        if (query.Count.HasValue) entities = entities.Take(query.Count.Value);
        return entities.ToArray();
    }

    public async Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        if (!type.Equals("PlatformLink", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("OrchestraMSP supports PlatformLink lookups only.");
        var clients = await client.ListClientsAsync(true, cancellationToken).ConfigureAwait(false);
        return clients.SelectMany(EnumerateLinks).Select(link => new EntitySyncLookup
        {
            Vendor = Vendor,
            Type = "PlatformLink",
            Id = link.PlatformInstanceId + ":" + link.ExternalId,
            Name = link.Platform,
            Properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlatformInstanceId"] = link.PlatformInstanceId,
                ["ExternalId"] = link.ExternalId,
                ["Status"] = link.Status,
                ["EntityType"] = link.EntityType,
                ["EntityId"] = link.EntityId.ToString("D")
            }
        }).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
        WriteAsync(request, true, cancellationToken);

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
        WriteAsync(request, false, cancellationToken);

    public Task<EntityWriteResult> LookupWriteByRequestIdAsync(
        EntityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = request.EntityType,
            Id = request.Id,
            VendorRequestId = request.VendorRequestId,
            Action = request.Id is null
                ? EntityAdapterActions.Create
                : EntityAdapterActions.Update,
            Success = false,
            RequestLookupOutcome = VendorRequestLookupOutcome.Unsupported,
            SafeCode = "REQUEST_ID_LOOKUP_UNSUPPORTED"
        });
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        _ = await client.ListClientsAsync(true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<CanonicalEntityVersion?> ReadCanonicalAsync(string entityType, Guid canonicalEntityId, long assertedVersion, CancellationToken cancellationToken)
    {
        if (canonicalEntityId == Guid.Empty) throw new ArgumentException("Canonical entity ID is required.", nameof(canonicalEntityId));
        if (assertedVersion <= 0) throw new ArgumentOutOfRangeException(nameof(assertedVersion));
        var entity = await ReadCurrentAsync(
            entityType, canonicalEntityId, cancellationToken).ConfigureAwait(false);
        if (entity is null) return null;
        if (!Guid.TryParse(entity.Id, out var observedId)
            || observedId == Guid.Empty
            || entity.Version is not > 0)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        return new CanonicalEntityVersion(observedId, entity.Version.Value, entity);
    }

    public async Task<EntityWriteParentResolution> ResolveWriteParentAsync(
        EntityWriteParentResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceVendor = Require(
            request.SourceVendor, nameof(request.SourceVendor));
        var platformInstanceId = Require(
            request.SourcePlatformInstanceId,
            nameof(request.SourcePlatformInstanceId));
        var parentId = Require(
            request.SourceParentEntityId,
            nameof(request.SourceParentEntityId));
        var parentType = Require(
            request.SourceParentEntityType,
            nameof(request.SourceParentEntityType));

        var clients = await client.ListClientsAsync(
            true, cancellationToken).ConfigureAwait(false);
        return parentType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            ? ResolveLinkedClients(
                clients, sourceVendor, platformInstanceId, parentId)
            : parentType.Equals("Site", StringComparison.OrdinalIgnoreCase)
                ? ResolveLinkedSites(
                    clients, sourceVendor, platformInstanceId, parentId)
                : ParentResolution(
                    EntityWriteParentResolutionStatus.Stale,
                    "ORCHESTRA_PARENT_LINK_STALE");
    }

    public async Task<ExternalPlatformLink?> LookupPlatformLinkAsync(string platformInstanceId, string externalId, CancellationToken cancellationToken)
    {
        platformInstanceId = Require(platformInstanceId, nameof(platformInstanceId));
        externalId = Require(externalId, nameof(externalId));
        var clients = await client.ListClientsAsync(true, cancellationToken).ConfigureAwait(false);
        var matches = clients.SelectMany(EnumerateLinks).Where(link => link.PlatformInstanceId.Equals(platformInstanceId, StringComparison.Ordinal) && link.ExternalId.Equals(externalId, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => OrchestraEntityMapper.MapLink(matches[0]),
            _ => throw new OrchestraDependencyException("ORCHESTRA_PLATFORM_LINK_CONFLICT")
        };
    }

    public async Task<ExternalPlatformLink> UpsertPlatformLinkAsync(OrchestraPlatformLinkCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        _ = await client.UpsertPlatformLinkAsync(command, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return await LookupPlatformLinkAsync(command.PlatformInstanceId, command.ExternalId, cancellationToken).ConfigureAwait(false)
               ?? throw new OrchestraDependencyException("ORCHESTRA_PLATFORM_LINK_READBACK_MISSING");
    }

    private async Task<EntityWriteResult> WriteAsync(EntityWriteRequest request, bool create, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!EntitySyncVendors.IsOrchestraMSP(request.Vendor)) throw new ArgumentException("Write request vendor must be OrchestraMSP.", nameof(request));
        var entityType = Require(request.EntityType, nameof(request.EntityType));
        var idempotencyKey = Require(request.IdempotencyKey ?? request.VendorRequestId, nameof(request.IdempotencyKey));
        var correlation = request.Correlation
            ?? throw new ArgumentException(
                "Canonical audit correlation is required for OrchestraMSP writes.",
                nameof(request));
        if (!create && request.ExpectedVersion is null)
            throw new ArgumentException(
                "An expected canonical version is required for updates.",
                nameof(request));
        var route = ResolveWriteRoute(request, create);
        var response = await client.SendWriteAsync(
            route.Method,
            route.Path,
            BuildCommand(request, route.Parents, create),
            idempotencyKey,
            correlation.CorrelationId,
            create ? null : request.ExpectedVersion,
            cancellationToken).ConfigureAwait(false);
        var resultElement = UnwrapResult(response);
        var identity = ReadIdentity(entityType, resultElement);
        var version = ReadVersion(resultElement);
        var observed = await ReadCanonicalAsync(entityType, identity, version, cancellationToken).ConfigureAwait(false);
        if (observed is null || observed.CanonicalEntityId != identity)
            throw new OrchestraDependencyException("ORCHESTRA_WRITE_READBACK_MISMATCH");
        if (entityType.Equals("Address", StringComparison.OrdinalIgnoreCase)
            && !AddressReadbackMatches(request, observed.Entity))
            throw new AmbiguousCanonicalWriteException(
                new InvalidOperationException(
                    "Authoritative address readback did not match the command."));
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = entityType,
            Id = identity.ToString("D"),
            VendorRequestId = request.VendorRequestId,
            RequestLookupOutcome = VendorRequestLookupOutcome.Applied,
            SafeCode = "OK",
            Action = create ? EntityAdapterActions.Create : EntityAdapterActions.Update,
            Success = true,
            Raw = observed.Entity
        };
    }

    private async Task<ExternalEntity?> ReadCurrentAsync(string entityType, Guid id, CancellationToken cancellationToken)
    {
        entityType = Require(entityType, nameof(entityType));
        if (entityType.Equals("Client", StringComparison.OrdinalIgnoreCase))
        {
            var found = await client.ReadClientAsync(id, cancellationToken).ConfigureAwait(false);
            return found is null ? null : OrchestraEntityMapper.MapClient(found);
        }
        var clients = await client.ListClientsAsync(true, cancellationToken).ConfigureAwait(false);
        if (entityType.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            var sites = clients.SelectMany(value => value.Sites).Where(value => value.SiteId == id).Take(2).ToArray();
            return sites.Length switch
            {
                0 => null,
                1 => OrchestraEntityMapper.MapSite(sites[0]),
                _ => throw new OrchestraDependencyException("ORCHESTRA_IDENTITY_CONFLICT")
            };
        }
        if (entityType.Equals("Address", StringComparison.OrdinalIgnoreCase))
            return MapUniqueAddresses(clients)
                .SingleOrDefault(value =>
                    value.Id.Equals(id.ToString("D"), StringComparison.Ordinal));
        throw new NotSupportedException("OrchestraMSP supports Client, Site, and Address entities.");
    }

    private static IEnumerable<OrchestraPlatformLinkContract> EnumerateLinks(OrchestraClientContract value) =>
        value.PlatformLinks.Concat(value.Sites.SelectMany(site => site.PlatformLinks)).Concat(value.Addresses.SelectMany(address => address.PlatformLinks)).Concat(value.Sites.SelectMany(site => site.Addresses.SelectMany(address => address.PlatformLinks)));

    private static IReadOnlyList<ExternalEntity> MapUniqueAddresses(
        IEnumerable<OrchestraClientContract> clients)
    {
        var result = new List<ExternalEntity>();
        var groups = clients
            .SelectMany(value => value.Addresses.Concat(
                value.Sites.SelectMany(site => site.Addresses)))
            .GroupBy(value => value.AddressId)
            .OrderBy(group => group.Key);
        foreach (var group in groups)
        {
            var mapped = group.Select(OrchestraEntityMapper.MapAddress).ToArray();
            var signatures = mapped
                .Select(value => JsonSerializer.Serialize(value))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (signatures.Length != 1)
                throw new OrchestraDependencyException(
                    "ORCHESTRA_IDENTITY_CONFLICT");
            result.Add(mapped[0]);
        }
        return result;
    }

    private static EntityWriteParentResolution ResolveLinkedClients(
        IEnumerable<OrchestraClientContract> clients,
        string sourceVendor,
        string platformInstanceId,
        string parentId)
    {
        var candidates = clients
            .SelectMany(value => value.PlatformLinks.Select(
                link => (Owner: value, Link: link)))
            .Where(value => LinkMatches(
                value.Link, sourceVendor, parentId, "Client"))
            .ToArray();
        var links = candidates
            .Where(value => value.Link.PlatformInstanceId.Equals(
                platformInstanceId, StringComparison.Ordinal))
            .ToArray();
        if (links.Length == 0)
            return ParentResolution(
                candidates.Length == 0
                    ? EntityWriteParentResolutionStatus.Missing
                    : EntityWriteParentResolutionStatus.Stale,
                candidates.Length == 0
                    ? "ORCHESTRA_PARENT_LINK_MISSING"
                    : "ORCHESTRA_PARENT_LINK_STALE");
        if (links.Any(value =>
                !IsUsable(value.Owner)
                || !IsUsable(value.Link)
                || value.Link.EntityId != value.Owner.ClientId))
            return ParentResolution(
                EntityWriteParentResolutionStatus.Stale,
                "ORCHESTRA_PARENT_LINK_STALE");
        if (links.Length != 1)
            return ParentResolution(
                EntityWriteParentResolutionStatus.Ambiguous,
                "ORCHESTRA_PARENT_LINK_AMBIGUOUS");
        var match = links[0];
        return new EntityWriteParentResolution(
            EntityWriteParentResolutionStatus.Resolved,
            new EntityWriteParent(
                match.Owner.ClientId,
                null,
                "Client",
                match.Link.PlatformInstanceId,
                match.Link.ExternalId,
                match.Link.Status,
                LinkToken(match.Link),
                match.Owner.Version),
            "OK");
    }

    private static EntityWriteParentResolution ResolveLinkedSites(
        IEnumerable<OrchestraClientContract> clients,
        string sourceVendor,
        string platformInstanceId,
        string parentId)
    {
        var candidates = clients
            .SelectMany(clientValue => clientValue.Sites.SelectMany(
                site => site.PlatformLinks.Select(link =>
                    (Client: clientValue, Site: site, Link: link))))
            .Where(value => LinkMatches(
                value.Link, sourceVendor, parentId, "Site"))
            .ToArray();
        var links = candidates
            .Where(value => value.Link.PlatformInstanceId.Equals(
                platformInstanceId, StringComparison.Ordinal))
            .ToArray();
        if (links.Length == 0)
            return ParentResolution(
                candidates.Length == 0
                    ? EntityWriteParentResolutionStatus.Missing
                    : EntityWriteParentResolutionStatus.Stale,
                candidates.Length == 0
                    ? "ORCHESTRA_PARENT_LINK_MISSING"
                    : "ORCHESTRA_PARENT_LINK_STALE");
        if (links.Any(value =>
                !IsUsable(value.Client)
                || !IsUsable(value.Site)
                || !IsUsable(value.Link)
                || value.Link.EntityId != value.Site.SiteId
                || value.Site.ClientId != value.Client.ClientId))
            return ParentResolution(
                EntityWriteParentResolutionStatus.Stale,
                "ORCHESTRA_PARENT_LINK_STALE");
        if (links.Length != 1)
            return ParentResolution(
                EntityWriteParentResolutionStatus.Ambiguous,
                "ORCHESTRA_PARENT_LINK_AMBIGUOUS");
        var match = links[0];
        return new EntityWriteParentResolution(
            EntityWriteParentResolutionStatus.Resolved,
            new EntityWriteParent(
                match.Client.ClientId,
                match.Site.SiteId,
                "Site",
                match.Link.PlatformInstanceId,
                match.Link.ExternalId,
                match.Link.Status,
                LinkToken(match.Link),
                match.Site.Version),
            "OK");
    }

    private static bool LinkMatches(
        OrchestraPlatformLinkContract link,
        string sourceVendor,
        string parentId,
        string entityType) =>
        link.Platform.Equals(
            EntitySyncVendors.Normalize(sourceVendor),
            StringComparison.OrdinalIgnoreCase)
        && link.ExternalId.Equals(parentId, StringComparison.Ordinal)
        && link.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(OrchestraClientContract value) =>
        value.Version > 0
        && !value.IsDeleted
        && value.MergedIntoClientId is null
        && value.LifecycleStatus.Equals(
            "active", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(OrchestraSiteContract value) =>
        value.Version > 0
        && !value.IsDeleted
        && value.LifecycleStatus.Equals(
            "active", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(OrchestraPlatformLinkContract value) =>
        value.Status.Equals("active", StringComparison.OrdinalIgnoreCase);

    private static string LinkToken(OrchestraPlatformLinkContract link)
    {
        var canonical = string.Join(
            '\n',
            link.PlatformInstanceId,
            EntitySyncVendors.Normalize(link.Platform),
            link.ExternalId,
            link.Status.ToLowerInvariant(),
            link.EntityType.ToLowerInvariant(),
            link.EntityId.ToString("D"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static EntityWriteParentResolution ParentResolution(
        EntityWriteParentResolutionStatus status,
        string code) =>
        new(status, null, code);

    private static object BuildCommand(
        EntityWriteRequest request,
        ParentIdentities parents,
        bool create)
    {
        var fields = BuildCustomFields(request);
        var correlation = ToCommandCorrelation(
            request.Correlation
            ?? throw new ArgumentException(
                "Canonical audit correlation is required for OrchestraMSP writes.",
                nameof(request)));
        var type = request.EntityType.Trim();
        if (type.Equals("Client", StringComparison.OrdinalIgnoreCase))
            return create
                ? new ClientCreateCommand(
                    request.Name, "active", fields, correlation)
                : new ClientUpdateCommand(request.Name, fields, correlation);
        if (type.Equals("Site", StringComparison.OrdinalIgnoreCase))
            return create
                ? new SiteCreateCommand(
                    parents.ClientId!.Value, request.Name, "active", fields, correlation)
                : new SiteUpdateCommand(
                    parents.ClientId!.Value, request.Name, fields, correlation);
        if (type.Equals("Address", StringComparison.OrdinalIgnoreCase))
        {
            var address = request.Address
                          ?? throw new OrchestraDependencyException(
                              "ORCHESTRA_ADDRESS_INVALID");
            var addressType = Require(
                address.AddressType ?? request.Name, "address_type");
            return create
                ? new AddressCreateCommand(
                    parents.ClientId!.Value,
                    parents.SiteId,
                    addressType,
                    "active",
                    address.Attention,
                    address.Line1,
                    address.Line2,
                    address.Line3,
                    address.City,
                    address.State,
                    address.PostalCode,
                    address.Country,
                    fields,
                    correlation)
                : new AddressUpdateCommand(
                    parents.ClientId!.Value,
                    parents.SiteId,
                    addressType,
                    address.Attention,
                    address.Line1,
                    address.Line2,
                    address.Line3,
                    address.City,
                    address.State,
                    address.PostalCode,
                    address.Country,
                    fields,
                    correlation);
        }
        throw new NotSupportedException(
            "OrchestraMSP supports Client, Site, and Address entities.");
    }

    private static OrchestraCommandCorrelation ToCommandCorrelation(
        EntityWriteCorrelation correlation) =>
        OrchestraCommandCorrelation.From(correlation);

    private static WriteRoute ResolveWriteRoute(
        EntityWriteRequest request,
        bool create)
    {
        var type = request.EntityType.Trim();
        if (type.Equals("Client", StringComparison.OrdinalIgnoreCase))
            return new WriteRoute(
                create ? HttpMethod.Post : HttpMethod.Patch,
                create ? "clients" : "clients/" + RequireGuid(request.Id, nameof(request.Id)),
                default);
        if (type.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                    request.ParentEntityType,
                    "Client",
                    StringComparison.OrdinalIgnoreCase))
                throw new OrchestraDependencyException(
                    "ORCHESTRA_PARENT_IDENTITY_INVALID");
            var clientId = ResolveRequiredIdentity(
                request.ParentId, request.ParentClientId);
            var path = create
                ? $"clients/{clientId:D}/sites"
                : $"clients/{clientId:D}/sites/{RequireGuid(request.Id, nameof(request.Id))}";
            return new WriteRoute(
                create ? HttpMethod.Post : HttpMethod.Patch,
                path,
                new ParentIdentities(clientId, null));
        }
        if (type.Equals("Address", StringComparison.OrdinalIgnoreCase))
        {
            var parentType = request.ParentEntityType?.Trim();
            Guid clientId;
            Guid? siteId;
            if (string.Equals(parentType, "Client", StringComparison.OrdinalIgnoreCase))
            {
                clientId = ResolveRequiredIdentity(
                    request.ParentId, request.ParentClientId);
                siteId = null;
            }
            else if (string.Equals(
                         parentType, "Site", StringComparison.OrdinalIgnoreCase))
            {
                clientId = ResolveRequiredIdentity(request.ParentClientId);
                siteId = ResolveRequiredIdentity(request.ParentId);
            }
            else
            {
                throw new OrchestraDependencyException(
                    "ORCHESTRA_PARENT_IDENTITY_INVALID");
            }
            var path = create
                ? "addresses"
                : "addresses/" + RequireGuid(request.Id, nameof(request.Id));
            return new WriteRoute(
                create ? HttpMethod.Post : HttpMethod.Patch,
                path,
                new ParentIdentities(clientId, siteId));
        }
        throw new NotSupportedException(
            "OrchestraMSP supports Client, Site, and Address entities.");
    }

    private static Guid ReadIdentity(string entityType, JsonElement value)
    {
        var property = entityType.Equals("Client", StringComparison.OrdinalIgnoreCase) ? "client_id" : entityType.Equals("Site", StringComparison.OrdinalIgnoreCase) ? "site_id" : entityType.Equals("Address", StringComparison.OrdinalIgnoreCase) ? "address_id" : throw new NotSupportedException();
        if (!value.TryGetProperty(property, out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var parsed) || parsed == Guid.Empty)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        return parsed;
    }

    private static long ReadVersion(JsonElement value)
    {
        if (!value.TryGetProperty("version", out var version) || !version.TryGetInt64(out var parsed) || parsed <= 0)
            throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        return parsed;
    }

    private static JsonElement UnwrapResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new OrchestraDependencyException("ORCHESTRA_CONTRACT_INVALID");
        if (value.TryGetProperty("result", out var result)) return result;
        if (value.TryGetProperty("entity", out var entity)) return entity;
        return value;
    }

    private static bool AddressReadbackMatches(
        EntityWriteRequest request,
        ExternalEntity observed)
    {
        if (!string.Equals(
                request.ParentEntityType,
                observed.ParentEntityType,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                request.ParentId,
                observed.ParentId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                request.ParentClientId,
                observed.GetExternalId("OrchestraClientId"),
                StringComparison.OrdinalIgnoreCase))
            return false;
        var expected = request.Address;
        var actual = observed.PrimaryAddress;
        return expected is not null
               && actual is not null
               && string.Equals(
                   expected.AddressType,
                   actual.AddressType,
                   StringComparison.Ordinal)
               && string.Equals(expected.Attention, actual.Attention, StringComparison.Ordinal)
               && string.Equals(expected.Line1, actual.Line1, StringComparison.Ordinal)
               && string.Equals(expected.Line2, actual.Line2, StringComparison.Ordinal)
               && string.Equals(expected.Line3, actual.Line3, StringComparison.Ordinal)
               && string.Equals(expected.City, actual.City, StringComparison.Ordinal)
               && string.Equals(expected.State, actual.State, StringComparison.Ordinal)
               && string.Equals(
                   expected.PostalCode, actual.PostalCode, StringComparison.Ordinal)
               && string.Equals(expected.Country, actual.Country, StringComparison.Ordinal);
    }

    private static SortedDictionary<string, object?> BuildCustomFields(
        EntityWriteRequest request)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in request.Fields
                     .Where(pair => !ReservedField(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            fields.Add(pair.Key, pair.Value);
        foreach (var pair in request.CustomFields
                     .Where(pair => !ReservedField(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            fields[pair.Key] = pair.Value;
        return fields;
    }

    private static Guid ResolveRequiredIdentity(params string?[] candidates)
    {
        Guid? identity = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!Guid.TryParse(candidate, out var parsed) || parsed == Guid.Empty)
                throw new OrchestraDependencyException(
                    "ORCHESTRA_PARENT_IDENTITY_INVALID");
            if (identity.HasValue && identity.Value != parsed)
                throw new OrchestraDependencyException(
                    "ORCHESTRA_PARENT_IDENTITY_INVALID");
            identity = parsed;
        }
        return identity
               ?? throw new OrchestraDependencyException(
                   "ORCHESTRA_PARENT_IDENTITY_INVALID");
    }

    private static string? ReadFieldString(
        IReadOnlyDictionary<string, object?> fields,
        string key) =>
        fields.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        } : null;

    private static bool ReservedField(string key) =>
        key.Equals("lifecycle_status", StringComparison.OrdinalIgnoreCase)
        || key.Equals("is_deleted", StringComparison.OrdinalIgnoreCase)
        || key.Equals("merged_into_client_id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("merged_from_client_ids", StringComparison.OrdinalIgnoreCase)
        || key.Equals("client_id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("site_id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OrchestraClientId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OrchestraSiteId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("version", StringComparison.OrdinalIgnoreCase)
        || key.Equals("address_type", StringComparison.OrdinalIgnoreCase)
        || key.Equals("attention", StringComparison.OrdinalIgnoreCase)
        || key.Equals("line1", StringComparison.OrdinalIgnoreCase)
        || key.Equals("line2", StringComparison.OrdinalIgnoreCase)
        || key.Equals("line3", StringComparison.OrdinalIgnoreCase)
        || key.Equals("city", StringComparison.OrdinalIgnoreCase)
        || key.Equals("state", StringComparison.OrdinalIgnoreCase)
        || key.Equals("postal_code", StringComparison.OrdinalIgnoreCase)
        || key.Equals("country", StringComparison.OrdinalIgnoreCase);

    private static string RequireGuid(string? value, string parameterName) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException(
                $"{parameterName} must be a UUID.", parameterName);

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"{parameterName} is required.", parameterName)
            : value.Trim();

    private readonly record struct ParentIdentities(Guid? ClientId, Guid? SiteId);
    private readonly record struct WriteRoute(
        HttpMethod Method,
        string Path,
        ParentIdentities Parents);

    private sealed record ClientCreateCommand(
        string Name,
        string LifecycleStatus,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);
    private sealed record ClientUpdateCommand(
        string Name,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);
    private sealed record SiteCreateCommand(
        Guid ClientId,
        string Name,
        string LifecycleStatus,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);
    private sealed record SiteUpdateCommand(
        Guid ClientId,
        string Name,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);
    private sealed record AddressCreateCommand(
        Guid ClientId,
        Guid? SiteId,
        string AddressType,
        string LifecycleStatus,
        string? Attention,
        string? Line1,
        string? Line2,
        string? Line3,
        string? City,
        string? State,
        string? PostalCode,
        string? Country,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);
    private sealed record AddressUpdateCommand(
        Guid ClientId,
        Guid? SiteId,
        string AddressType,
        string? Attention,
        string? Line1,
        string? Line2,
        string? Line3,
        string? City,
        string? State,
        string? PostalCode,
        string? Country,
        IReadOnlyDictionary<string, object?> Fields,
        OrchestraCommandCorrelation Correlation);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose();
    }
}
