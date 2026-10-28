using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warehouse.Node.Controllers;
using Warehouse.Node.Models;
using Warehouse.Node.Services;
using Xunit;

namespace Warehouse.Tests;

public class SalesApiTests
{
    [Fact]
    public async Task CrudLifecycle_Json_WorksWithZeroVerbRoutesAndDiagnosticHeaders()
    {
        // Pick an ID that hashes to shard 0 (local shard for api-1)
        string localId = Enumerable.Range(1, 100)
            .Select(i => $"sale-local-{i}")
            .First(id => ShardHelper.GetShard(id, 3) == 0);

        await using var app = CreateNodeApp();
        using var client = app.CreateClient();

        var createPayload = new SaleRecord
        {
            Id = localId,
            Region = "EU",
            Category = "Electronics",
            Product = "Monitor 4K",
            Quantity = 3,
            UnitPrice = 400m
        };

        var postResp = await client.PostAsJsonAsync("/api/sales", createPayload);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
        Assert.Equal("api-1", postResp.Headers.GetValues("X-Served-By").Single());
        Assert.Equal("0", postResp.Headers.GetValues("X-Owner-Shard").Single());

        var created = await postResp.Content.ReadFromJsonAsync<SaleRecord>();
        Assert.NotNull(created);
        Assert.Equal(1200m, created!.TotalAmount);

        var getResp = await client.GetAsync($"/api/sales/{localId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<SaleRecord>();
        Assert.Equal("Monitor 4K", fetched!.Product);

        createPayload.Quantity = 5;
        var putResp = await client.PutAsJsonAsync($"/api/sales/{localId}", createPayload);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<SaleRecord>();
        Assert.Equal(2000m, updated!.TotalAmount);

        var delResp = await client.DeleteAsync($"/api/sales/{localId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var afterDel = await client.GetAsync($"/api/sales/{localId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDel.StatusCode);
    }

    [Fact]
    public async Task PostAndReport_XmlContentNegotiation_RoundTripsCleanly()
    {
        string localId = Enumerable.Range(1, 100)
            .Select(i => $"sale-xml-{i}")
            .First(id => ShardHelper.GetShard(id, 3) == 0);

        var peer = new StubPeerShardClient();
        peer.ShardRecords[1] = (true, []);
        peer.ShardRecords[2] = (true, []);

        await using var app = CreateNodeApp(peer);
        using var client = app.CreateClient();

        var xmlRecord = new SaleRecord
        {
            Id = localId,
            Region = "US",
            Category = "Software",
            Product = "Cloud License",
            Quantity = 4,
            UnitPrice = 250m
        };

        var postReq = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = new StringContent(SerializeXml(xmlRecord), Encoding.UTF8, "application/xml")
        };
        postReq.Headers.Accept.ParseAdd("application/xml");

        var postResp = await client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
        Assert.Contains("xml", postResp.Content.Headers.ContentType?.MediaType ?? string.Empty);

        var createdXml = DeserializeXml<SaleRecord>(await postResp.Content.ReadAsStringAsync());
        Assert.Equal(1000m, createdXml.TotalAmount);

        var reportReq = new HttpRequestMessage(HttpMethod.Get, "/api/sales/reports");
        reportReq.Headers.Accept.ParseAdd("application/xml");
        var reportResp = await client.SendAsync(reportReq);
        Assert.Equal(HttpStatusCode.OK, reportResp.StatusCode);
        Assert.Contains("xml", reportResp.Content.Headers.ContentType?.MediaType ?? string.Empty);

        var report = DeserializeXml<SalesReport>(await reportResp.Content.ReadAsStringAsync());
        Assert.Equal(1, report.TotalRecords);
        Assert.Equal(1000m, report.GrandTotalRevenue);
    }

    [Fact]
    public async Task InternalShardHopHeader_PreventsInfiniteForwardingLoop()
    {
        string remoteId = Enumerable.Range(1, 100)
            .Select(i => $"sale-remote-{i}")
            .First(id => ShardHelper.GetShard(id, 3) == 2);

        var peer = new StubPeerShardClient();
        await using var app = CreateNodeApp(peer);
        using var client = app.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = JsonContent.Create(new SaleRecord
            {
                Id = remoteId,
                Region = "APAC",
                Category = "Hardware",
                Product = "Router",
                Quantity = 1,
                UnitPrice = 300m
            })
        };
        req.Headers.Add("X-Internal-Shard-Hop", "true");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(0, peer.ForwardCalls);
    }

    [Fact]
    public async Task ScatterGather_MergesHealthyShardsAndMarksDegradedShards()
    {
        string localId = Enumerable.Range(1, 100)
            .Select(i => $"sale-s0-{i}")
            .First(id => ShardHelper.GetShard(id, 3) == 0);

        var peer = new StubPeerShardClient();
        peer.ShardRecords[1] = (true, [new SaleRecord { Id = "sale-s1", Region = "US", Category = "Tech", Product = "SSD", Quantity = 2, UnitPrice = 100m, TotalAmount = 200m, ShardId = 1 }]);
        peer.ShardRecords[2] = (false, []); // shard 2 offline

        await using var app = CreateNodeApp(peer);
        using var client = app.CreateClient();

        await client.PostAsJsonAsync("/api/sales", new SaleRecord
        {
            Id = localId,
            Region = "EU",
            Category = "Tech",
            Product = "RAM",
            Quantity = 1,
            UnitPrice = 150m
        });

        var listResp = await client.GetAsync("/api/sales");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        Assert.Equal("2", listResp.Headers.GetValues("X-Degraded-Shards").Single());

        var all = await listResp.Content.ReadFromJsonAsync<List<SaleRecord>>();
        Assert.Equal(2, all!.Count);
    }

    [Fact]
    public async Task FaultInjectionHeader_DelaysOnlyMatchingNode()
    {
        var peer = new StubPeerShardClient();
        peer.ShardRecords[1] = (true, []);
        peer.ShardRecords[2] = (true, []);

        await using var app = CreateNodeApp(peer);
        using var client = app.CreateClient();

        var fastSw = Stopwatch.StartNew();
        var fastReq = new HttpRequestMessage(HttpMethod.Get, "/api/sales");
        fastReq.Headers.Add("X-Simulate-Fail-Node", "api-2");
        fastReq.Headers.Add("X-Simulate-Delay-Ms", "200");
        var fastResp = await client.SendAsync(fastReq);
        fastSw.Stop();
        Assert.Equal(HttpStatusCode.OK, fastResp.StatusCode);
        Assert.True(fastSw.ElapsedMilliseconds < 150);

        var slowSw = Stopwatch.StartNew();
        var slowReq = new HttpRequestMessage(HttpMethod.Get, "/api/sales");
        slowReq.Headers.Add("X-Simulate-Fail-Node", "api-1");
        slowReq.Headers.Add("X-Simulate-Delay-Ms", "160");
        var slowResp = await client.SendAsync(slowReq);
        slowSw.Stop();
        Assert.Equal(HttpStatusCode.OK, slowResp.StatusCode);
        Assert.True(slowSw.ElapsedMilliseconds >= 140);
    }

    private static WebApplicationFactory<SalesController> CreateNodeApp(StubPeerShardClient? peer = null)
    {
        var repo = new InMemoryWarehouseRepository();
        var peerClient = peer ?? new StubPeerShardClient();

        return new WebApplicationFactory<SalesController>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NodeOptions>();
                services.AddSingleton(new NodeOptions
                {
                    NodeId = "api-1",
                    ShardId = 0,
                    ShardCount = 3,
                    EnableConsul = false
                });
                services.RemoveAll<IWarehouseRepository>();
                services.AddSingleton<IWarehouseRepository>(repo);
                services.RemoveAll<IPeerShardClient>();
                services.AddSingleton<IPeerShardClient>(peerClient);
            });
        });
    }

    private static string SerializeXml<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var xmlWriter = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            new XmlSerializer(typeof(T)).Serialize(xmlWriter, value);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static T DeserializeXml<T>(string xml)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var reader = new StringReader(xml);
        return (T)serializer.Deserialize(reader)!;
    }

    private sealed class InMemoryWarehouseRepository : IWarehouseRepository
    {
        private readonly ConcurrentDictionary<string, SaleRecord> _items = new();

        public Task<List<SaleRecord>> GetAllAsync(string? region, string? category, CancellationToken ct)
        {
            var query = _items.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(region))
                query = query.Where(r => string.Equals(r.Region, region, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(query.OrderBy(r => r.Id).ToList());
        }

        public Task<SaleRecord?> GetByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(_items.TryGetValue(id, out var r) ? r : null);

        public Task UpsertAsync(SaleRecord record, CancellationToken ct)
        {
            _items[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct) =>
            Task.FromResult(_items.TryRemove(id, out _));

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class StubPeerShardClient : IPeerShardClient
    {
        public int ForwardCalls { get; private set; }
        public Dictionary<int, (bool Ok, List<SaleRecord> Records)> ShardRecords { get; } = new();

        public Task<PeerResponse> ForwardSingleAsync(int targetShard, HttpRequest incomingRequest, byte[] bodyBytes, CancellationToken ct)
        {
            ForwardCalls++;
            return Task.FromResult(new PeerResponse(503, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"offline\"}"), null));
        }

        public Task<(bool Ok, List<SaleRecord> Records)> FetchShardRecordsAsync(int targetShard, string? region, string? category, CancellationToken ct)
        {
            if (ShardRecords.TryGetValue(targetShard, out var entry))
                return Task.FromResult(entry);
            return Task.FromResult((true, new List<SaleRecord>()));
        }
    }
}
