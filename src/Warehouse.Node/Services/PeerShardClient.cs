using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Consul;
using Microsoft.AspNetCore.Http;
using Warehouse.Node.Models;

namespace Warehouse.Node.Services;

public class PeerShardClient : IPeerShardClient
{
    private readonly IConsulClient _consul;
    private readonly IHttpClientFactory _httpFactory;

    public PeerShardClient(IConsulClient consul, IHttpClientFactory httpFactory)
    {
        _consul = consul;
        _httpFactory = httpFactory;
    }

    public async Task<PeerResponse> ForwardSingleAsync(int targetShard, HttpRequest incomingRequest, byte[] bodyBytes, CancellationToken ct)
    {
        var baseUrl = await ResolveShardBaseUrlAsync(targetShard, ct);
        if (baseUrl is null)
        {
            var err = Encoding.UTF8.GetBytes($"{{\"error\":\"Shard {targetShard} is currently unavailable\"}}");
            return new PeerResponse(StatusCodes.Status503ServiceUnavailable, "application/json", err, null);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var url = $"{baseUrl}{incomingRequest.Path}{incomingRequest.QueryString}";
            using var req = new HttpRequestMessage(new HttpMethod(incomingRequest.Method), url);
            req.Headers.TryAddWithoutValidation("X-Internal-Shard-Hop", "true");

            if (incomingRequest.Headers.TryGetValue("Accept", out var accept) && !string.IsNullOrWhiteSpace(accept))
                req.Headers.TryAddWithoutValidation("Accept", accept.ToString());

            if (bodyBytes.Length > 0)
            {
                req.Content = new ByteArrayContent(bodyBytes);
                var ctHeader = incomingRequest.ContentType ?? "application/json";
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctHeader.Split(';')[0].Trim());
            }

            var client = _httpFactory.CreateClient("peer");
            using var resp = await client.SendAsync(req, cts.Token);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            var contentType = resp.Content.Headers.ContentType?.ToString();
            var servedBy = resp.Headers.TryGetValues("X-Served-By", out var vals) ? vals.FirstOrDefault() : null;
            return new PeerResponse((int)resp.StatusCode, contentType, bytes, servedBy);
        }
        catch (Exception ex)
        {
            var err = Encoding.UTF8.GetBytes($"{{\"error\":\"Peer shard {targetShard} error: {ex.Message}\"}}");
            return new PeerResponse(StatusCodes.Status503ServiceUnavailable, "application/json", err, null);
        }
    }

    public async Task<(bool Ok, List<SaleRecord> Records)> FetchShardRecordsAsync(int targetShard, string? region, string? category, CancellationToken ct)
    {
        var baseUrl = await ResolveShardBaseUrlAsync(targetShard, ct);
        if (baseUrl is null)
            return (false, []);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(region))
                queryParts.Add($"region={Uri.EscapeDataString(region)}");
            if (!string.IsNullOrWhiteSpace(category))
                queryParts.Add($"category={Uri.EscapeDataString(category)}");
            var qs = queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : string.Empty;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sales{qs}");
            req.Headers.TryAddWithoutValidation("X-Internal-Shard-Hop", "true");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var client = _httpFactory.CreateClient("peer");
            using var resp = await client.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return (false, []);

            var items = await resp.Content.ReadFromJsonAsync<List<SaleRecord>>(cancellationToken: cts.Token) ?? [];
            return (true, items);
        }
        catch
        {
            return (false, []);
        }
    }

    private async Task<string?> ResolveShardBaseUrlAsync(int targetShard, CancellationToken ct)
    {
        try
        {
            var res = await _consul.Health.Service("warehouse-node", $"shard:{targetShard}", passingOnly: true, ct);
            var svc = res.Response?.FirstOrDefault()?.Service;
            if (svc is null || string.IsNullOrWhiteSpace(svc.Address))
                return null;
            return $"http://{svc.Address}:{svc.Port}";
        }
        catch
        {
            return null;
        }
    }
}
