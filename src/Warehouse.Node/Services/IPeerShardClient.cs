using Microsoft.AspNetCore.Http;
using Warehouse.Node.Models;

namespace Warehouse.Node.Services;

public record PeerResponse(int StatusCode, string? ContentType, byte[] Body, string? ServedByNode);

public interface IPeerShardClient
{
    Task<PeerResponse> ForwardSingleAsync(int targetShard, HttpRequest incomingRequest, byte[] bodyBytes, CancellationToken ct);
    Task<(bool Ok, List<SaleRecord> Records)> FetchShardRecordsAsync(int targetShard, string? region, string? category, CancellationToken ct);
}
