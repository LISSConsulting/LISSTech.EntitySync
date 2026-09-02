using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IHaloSourceWritebackAdapter
{
    Task<EntityWriteResult> UpsertNCentralClientLinkAsync(
        string haloClientId,
        string haloClientName,
        string nCentralCustomerId,
        string nCentralCustomerName,
        CancellationToken cancellationToken);

    Task<EntityWriteResult> UpsertNCentralSiteLinkAsync(
        string haloSiteId,
        string haloSiteName,
        string haloClientName,
        string nCentralSiteId,
        string nCentralSiteName,
        string nCentralCustomerId,
        CancellationToken cancellationToken);
}
