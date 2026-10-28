using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;

namespace Warehouse.Gateway;

public class CacheOptions
{
    public int TtlSeconds { get; set; } = 30;
}

public record CachedGatewayResponse(byte[] Body, string ContentType, string? ServedBy, string? OwnerShard);

public interface ICacheStore
{
    Task<(CachedGatewayResponse? Entry, int? TtlSeconds)> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, CachedGatewayResponse value, TimeSpan ttl, CancellationToken ct);
    Task InvalidateByPrefixAsync(string prefix, CancellationToken ct);
}

public class RedisCacheStore : ICacheStore
{
    private const string IndexSetKey = "cache:keys:index";
    private readonly IConnectionMultiplexer _redis;

    public RedisCacheStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<(CachedGatewayResponse? Entry, int? TtlSeconds)> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var valTask = db.StringGetAsync(key);
            var ttlTask = db.KeyTimeToLiveAsync(key);
            await Task.WhenAll(valTask, ttlTask);

            if (!valTask.Result.HasValue)
                return (null, null);

            var entry = JsonSerializer.Deserialize<CachedGatewayResponse>((byte[])valTask.Result!);
            int? ttl = ttlTask.Result.HasValue ? Math.Max(1, (int)ttlTask.Result.Value.TotalSeconds) : null;
            return (entry, ttl);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task SetAsync(string key, CachedGatewayResponse value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await db.StringSetAsync(key, bytes, ttl);
            await db.SetAddAsync(IndexSetKey, key);
        }
        catch
        {
            // Degrade gracefully if Redis is transiently unreachable
        }
    }

    public async Task InvalidateByPrefixAsync(string prefix, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var members = await db.SetMembersAsync(IndexSetKey);
            var matching = members
                .Select(m => m.ToString())
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

            if (matching.Length > 0)
            {
                await db.KeyDeleteAsync(matching.Select(k => (RedisKey)k).ToArray());
                await db.SetRemoveAsync(IndexSetKey, matching.Select(k => (RedisValue)k).ToArray());
            }
        }
        catch
        {
            // Degrade gracefully if Redis is transiently unreachable
        }
    }
}

public class RedisCacheMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ICacheStore _store;
    private readonly TimeSpan _ttl;

    public RedisCacheMiddleware(RequestDelegate next, ICacheStore store, CacheOptions options)
    {
        _next = next;
        _store = store;
        _ttl = TimeSpan.FromSeconds(Math.Max(1, options.TtlSeconds));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        string method = context.Request.Method;

        if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method))
        {
            await _next(context);
            if (context.Response.StatusCode is >= 200 and < 300)
                await _store.InvalidateByPrefixAsync("cache:/api/", context.RequestAborted);
            return;
        }

        if (!HttpMethods.IsGet(method) || ShouldBypassCache(context.Request))
        {
            await _next(context);
            return;
        }

        string format = context.Request.Headers.Accept.ToString().Contains("xml", StringComparison.OrdinalIgnoreCase)
            ? "xml"
            : "json";
        string key = $"cache:{context.Request.Path}{context.Request.QueryString}:fmt={format}";

        var (cached, remainingTtl) = await _store.GetAsync(key, context.RequestAborted);
        if (cached is not null)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = cached.ContentType;
            context.Response.Headers["X-Cache"] = "HIT";
            if (remainingTtl.HasValue)
                context.Response.Headers["X-Cache-TTL"] = remainingTtl.Value.ToString();
            if (!string.IsNullOrWhiteSpace(cached.ServedBy))
                context.Response.Headers["X-Served-By"] = cached.ServedBy;
            if (!string.IsNullOrWhiteSpace(cached.OwnerShard))
                context.Response.Headers["X-Owner-Shard"] = cached.OwnerShard;

            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            buffer.Position = 0;
            var bodyBytes = buffer.ToArray();

            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey("X-Degraded-Shards"))
            {
                context.Response.Headers["X-Cache"] = "MISS";
                var entry = new CachedGatewayResponse(
                    bodyBytes,
                    context.Response.ContentType ?? (format == "xml" ? "application/xml" : "application/json"),
                    context.Response.Headers.TryGetValue("X-Served-By", out var sb) ? sb.ToString() : null,
                    context.Response.Headers.TryGetValue("X-Owner-Shard", out var os) ? os.ToString() : null);

                await _store.SetAsync(key, entry, _ttl, context.RequestAborted);
            }
            else if (context.Response.Headers.ContainsKey("X-Degraded-Shards"))
            {
                context.Response.Headers["X-Cache"] = "BYPASS";
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldBypassCache(HttpRequest request) =>
        request.Headers.ContainsKey("X-Simulate-Fail-Node")
        || request.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);
}
