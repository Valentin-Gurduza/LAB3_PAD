using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Node.Models;
using Warehouse.Node.Services;

namespace Warehouse.Node.Controllers;

[ApiController]
[Route("api/sales")]
public class SalesController : ControllerBase
{
    private readonly IWarehouseRepository _repo;
    private readonly IPeerShardClient _peerClient;
    private readonly NodeOptions _options;

    public SalesController(IWarehouseRepository repo, IPeerShardClient peerClient, NodeOptions options)
    {
        _repo = repo;
        _peerClient = peerClient;
        _options = options;
    }

    [HttpGet]
    public async Task<ActionResult<List<SaleRecord>>> GetList(
        [FromQuery] string? region,
        [FromQuery] string? category,
        CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        var (records, _) = await GatherRecordsAsync(region, category, ct);
        return Ok(records);
    }

    [HttpGet("reports")]
    public async Task<ActionResult<SalesReport>> GetReports(
        [FromQuery] string? region,
        [FromQuery] string? category,
        CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        var (records, activeShards) = await GatherRecordsAsync(region, category, ct);
        return Ok(SalesReport.FromRecords(records, activeShards));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        int ownerShard = ShardHelper.GetShard(id, _options.ShardCount);
        Response.Headers["X-Owner-Shard"] = ownerShard.ToString();

        if (ownerShard != _options.ShardId && !IsInternalHop())
        {
            var peerResp = await _peerClient.ForwardSingleAsync(ownerShard, Request, [], ct);
            return ToActionResult(peerResp);
        }

        var item = await _repo.GetByIdAsync(id, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaleRecord input, CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        if (string.IsNullOrWhiteSpace(input.Id))
            input.Id = $"sale-{Guid.NewGuid():N}"[..13];

        int ownerShard = ShardHelper.GetShard(input.Id, _options.ShardCount);
        input.ShardId = ownerShard;
        if (input.RecordedAt == default)
            input.RecordedAt = DateTime.UtcNow;
        input.Recalculate();

        Response.Headers["X-Owner-Shard"] = ownerShard.ToString();

        if (ownerShard != _options.ShardId && !IsInternalHop())
        {
            var peerResp = await _peerClient.ForwardSingleAsync(ownerShard, Request, SerializePayload(input), ct);
            return ToActionResult(peerResp);
        }

        await _repo.UpsertAsync(input, ct);
        return StatusCode(StatusCodes.Status201Created, input);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] SaleRecord input, CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        input.Id = id;
        int ownerShard = ShardHelper.GetShard(id, _options.ShardCount);
        input.ShardId = ownerShard;
        if (input.RecordedAt == default)
            input.RecordedAt = DateTime.UtcNow;
        input.Recalculate();

        Response.Headers["X-Owner-Shard"] = ownerShard.ToString();

        if (ownerShard != _options.ShardId && !IsInternalHop())
        {
            var peerResp = await _peerClient.ForwardSingleAsync(ownerShard, Request, SerializePayload(input), ct);
            return ToActionResult(peerResp);
        }

        var existing = await _repo.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        await _repo.UpsertAsync(input, ct);
        return Ok(input);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id, CancellationToken ct)
    {
        await MaybeSimulateFaultAsync(ct);
        Response.Headers["X-Served-By"] = _options.NodeId;

        int ownerShard = ShardHelper.GetShard(id, _options.ShardCount);
        Response.Headers["X-Owner-Shard"] = ownerShard.ToString();

        if (ownerShard != _options.ShardId && !IsInternalHop())
        {
            var peerResp = await _peerClient.ForwardSingleAsync(ownerShard, Request, [], ct);
            return ToActionResult(peerResp);
        }

        bool deleted = await _repo.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    private async Task<(List<SaleRecord> Records, List<int> ActiveShards)> GatherRecordsAsync(
        string? region,
        string? category,
        CancellationToken ct)
    {
        var local = await _repo.GetAllAsync(region, category, ct);
        if (IsInternalHop())
            return (local, [_options.ShardId]);

        var peerShards = Enumerable.Range(0, _options.ShardCount)
            .Where(s => s != _options.ShardId)
            .ToList();

        var peerTasks = peerShards
            .Select(async shard => (Shard: shard, Result: await _peerClient.FetchShardRecordsAsync(shard, region, category, ct)))
            .ToList();

        var peerResults = await Task.WhenAll(peerTasks);

        var combined = new List<SaleRecord>(local);
        var activeShards = new List<int> { _options.ShardId };
        var degradedShards = new List<int>();

        foreach (var entry in peerResults)
        {
            if (entry.Result.Ok)
            {
                activeShards.Add(entry.Shard);
                combined.AddRange(entry.Result.Records);
            }
            else
            {
                degradedShards.Add(entry.Shard);
            }
        }

        if (degradedShards.Count > 0)
            Response.Headers["X-Degraded-Shards"] = string.Join(",", degradedShards.OrderBy(s => s));

        return (combined.OrderBy(r => r.Id).ToList(), activeShards);
    }

    private bool IsInternalHop() =>
        Request.Headers.TryGetValue("X-Internal-Shard-Hop", out var val)
        && string.Equals(val.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    private async Task MaybeSimulateFaultAsync(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Simulate-Fail-Node", out var target))
            return;

        string targetVal = target.ToString().Trim();
        if (!string.Equals(targetVal, _options.NodeId, StringComparison.OrdinalIgnoreCase) && targetVal != "*")
            return;

        int delayMs = 4000;
        if (Request.Headers.TryGetValue("X-Simulate-Delay-Ms", out var rawDelay)
            && int.TryParse(rawDelay.ToString(), out int parsed))
        {
            delayMs = Math.Clamp(parsed, 1, 10000);
        }

        await Task.Delay(delayMs, ct);
    }

    private byte[] SerializePayload(SaleRecord record)
    {
        if ((Request.ContentType ?? string.Empty).Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            using (var xmlWriter = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                new XmlSerializer(typeof(SaleRecord)).Serialize(xmlWriter, record);
            }
            return ms.ToArray();
        }

        return JsonSerializer.SerializeToUtf8Bytes(record);
    }

    private IActionResult ToActionResult(PeerResponse peer)
    {
        if (!string.IsNullOrWhiteSpace(peer.ServedByNode))
            Response.Headers["X-Forwarded-To"] = peer.ServedByNode;

        if (peer.StatusCode == StatusCodes.Status204NoContent || peer.Body.Length == 0)
            return StatusCode(peer.StatusCode);

        Response.StatusCode = peer.StatusCode;
        return File(peer.Body, peer.ContentType ?? "application/json");
    }
}
