using Tide.Application.Actions;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>For a test that closes a sweep or hunt as no hits: a query on record, run, that found nothing, as closing it
/// so needs.</summary>
public static class HuntOnRecord
{
    public static async Task RanCleanAsync(this ActionService svc, Guid actionId)
    {
        var hunt = await svc.AddHandWrittenHuntAsync(actionId, "kql", "DeviceNetworkEvents | where RemoteIP == \"203.0.113.7\"");
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, null, null);
    }
}
