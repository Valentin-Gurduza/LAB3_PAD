using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Gateway;
using Xunit;

namespace Warehouse.Tests;

public class GatewayMiddlewareTests
{
    [Fact]
    public async Task RedisCacheMiddleware_SeparatesJsonAndXmlAndInvalidatesOnWrite()
    {
        var store = new InMemoryCacheStore();
        int downstreamCalls = 0;

        RequestDelegate terminal = async ctx =>
        {
            Interlocked.Increment(ref downstreamCalls);
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                return;
            }

            bool wantsXml = ctx.Request.Headers.Accept.ToString().Contains("xml", StringComparison.OrdinalIgnoreCase);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = wantsXml ? "application/xml" : "application/json";
            ctx.Response.Headers["X-Served-By"] = "api-2";
            ctx.Response.Headers["X-Owner-Shard"] = "1";
            var payload = wantsXml ? "<ArrayOfSaleRecord />" : "[{\"id\":\"sale-1\"}]";
            await ctx.Response.WriteAsync(payload);
        };

        var middleware = new RedisCacheMiddleware(terminal, store, new CacheOptions { TtlSeconds = 30 });

        var (jsonMissStatus, jsonMissBody, jsonMissCache, _) = await InvokeAsync(middleware, "GET", "/api/sales", "application/json");
        Assert.Equal(200, jsonMissStatus);
        Assert.Equal("MISS", jsonMissCache);
        Assert.Equal("[{\"id\":\"sale-1\"}]", jsonMissBody);
        Assert.Equal(1, downstreamCalls);

        var (_, jsonHitBody, jsonHitCache, jsonHitServedBy) = await InvokeAsync(middleware, "GET", "/api/sales", "application/json");
        Assert.Equal("HIT", jsonHitCache);
        Assert.Equal("[{\"id\":\"sale-1\"}]", jsonHitBody);
        Assert.Equal("api-2", jsonHitServedBy);
        Assert.Equal(1, downstreamCalls);

        var (_, xmlMissBody, xmlMissCache, _) = await InvokeAsync(middleware, "GET", "/api/sales", "application/xml");
        Assert.Equal("MISS", xmlMissCache);
        Assert.Equal("<ArrayOfSaleRecord />", xmlMissBody);
        Assert.Equal(2, downstreamCalls);

        var (_, xmlHitBody, xmlHitCache, _) = await InvokeAsync(middleware, "GET", "/api/sales", "application/xml");
        Assert.Equal("HIT", xmlHitCache);
        Assert.Equal("<ArrayOfSaleRecord />", xmlHitBody);
        Assert.Equal(2, downstreamCalls);

        await InvokeAsync(middleware, "POST", "/api/sales", "application/json");
        Assert.Equal(3, downstreamCalls);

        var (_, afterWriteBody, afterWriteCache, _) = await InvokeAsync(middleware, "GET", "/api/sales", "application/json");
        Assert.Equal("MISS", afterWriteCache);
        Assert.Equal("[{\"id\":\"sale-1\"}]", afterWriteBody);
        Assert.Equal(4, downstreamCalls);
    }

    [Fact]
    public async Task PriorityQueueMiddleware_ProcessesHighBeforeLowAndHandlesUnknownHeader()
    {
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new List<string>();
        var orderLock = new object();

        RequestDelegate terminal = async ctx =>
        {
            string tag = ctx.Items["tag"]!.ToString()!;
            lock (orderLock)
            {
                executionOrder.Add(tag);
            }

            if (tag == "blocker")
                await releaseBlocker.Task;
            else
                await Task.Yield();

            ctx.Response.StatusCode = 200;
        };

        var middleware = new PriorityQueueMiddleware(terminal, new PriorityOptions { MaxConcurrent = 1 });

        var blockerCtx = MakePriorityContext("blocker", "normal");
        var blockerTask = middleware.InvokeAsync(blockerCtx);

        var lowCtx = MakePriorityContext("low", "low");
        var normalCtx = MakePriorityContext("normal", "normal");
        var highCtx = MakePriorityContext("high", "high");
        var urgentCtx = MakePriorityContext("urgent", "urgent");

        var lowTask = middleware.InvokeAsync(lowCtx);
        var normalTask = middleware.InvokeAsync(normalCtx);
        var highTask = middleware.InvokeAsync(highCtx);
        var urgentTask = middleware.InvokeAsync(urgentCtx);

        releaseBlocker.SetResult();
        await Task.WhenAll(blockerTask, lowTask, normalTask, highTask, urgentTask);

        Assert.Equal(["blocker", "high", "normal", "urgent", "low"], executionOrder);
        Assert.Equal("high", highCtx.Response.Headers["X-Priority-Applied"].ToString());
        Assert.Equal("normal", urgentCtx.Response.Headers["X-Priority-Applied"].ToString());
        Assert.Equal("low", lowCtx.Response.Headers["X-Priority-Applied"].ToString());
    }

    [Fact]
    public async Task OcelotConfigAndConsulServiceBuilder_ValidateAndResolveCleanly()
    {
        string gatewayDir = Path.GetFullPath("../../../../../src/Warehouse.Gateway", AppContext.BaseDirectory);
        var webBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ContentRootPath = gatewayDir,
            EnvironmentName = "Production"
        });
        Microsoft.Extensions.Configuration.JsonConfigurationExtensions.AddJsonFile(
            webBuilder.Configuration, Path.Combine(gatewayDir, "ocelot.json"), optional: false, reloadOnChange: false);
        Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            webBuilder.Configuration,
            new Dictionary<string, string?>
            {
                ["GlobalConfiguration:ServiceDiscoveryProvider:Host"] = "custom-consul-host",
                ["GlobalConfiguration:ServiceDiscoveryProvider:Port"] = "8501"
            });

        var ocelotBuilder = Ocelot.DependencyInjection.ServiceCollectionExtensions
            .AddOcelot(webBuilder.Services, webBuilder.Configuration);
        Ocelot.Provider.Consul.OcelotBuilderExtensions.AddConsul<CustomConsulServiceBuilder>(ocelotBuilder);
        Ocelot.Provider.Polly.OcelotBuilderExtensions.AddPolly(ocelotBuilder);

        await using var app = webBuilder.Build();
        var monitor = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Ocelot.Configuration.File.FileConfiguration>>();
        Assert.Equal("custom-consul-host", monitor.CurrentValue.GlobalConfiguration.ServiceDiscoveryProvider.Host);
        Assert.Equal(8501, monitor.CurrentValue.GlobalConfiguration.ServiceDiscoveryProvider.Port);

        var creator = app.Services.GetRequiredService<Ocelot.Configuration.Creator.IInternalConfigurationCreator>();
        var internalCfg = await creator.Create(monitor.CurrentValue);
        Assert.False(internalCfg.IsError, string.Join("; ", internalCfg.Errors.Select(e => e.Message)));
        Assert.Equal("custom-consul-host", internalCfg.Data.ServiceProviderConfiguration.Host);

        var internalRepo = app.Services.GetRequiredService<Ocelot.Configuration.Repository.IInternalConfigurationRepository>();
        internalRepo.AddOrReplace(internalCfg.Data);

        var serviceDescriptor = webBuilder.Services.Single(d => d.ServiceType == typeof(Ocelot.Provider.Consul.Interfaces.IConsulServiceBuilder));
        Assert.Equal(typeof(CustomConsulServiceBuilder), serviceDescriptor.ImplementationType);

        await Ocelot.Middleware.OcelotMiddlewareExtensions.UseOcelot(app);
        Assert.Equal("custom-consul-host", internalRepo.Get().Data.ServiceProviderConfiguration.Host);
    }

    private static DefaultHttpContext MakePriorityContext(string tag, string priorityHeader)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/sales";
        ctx.Request.Headers["X-Priority"] = priorityHeader;
        ctx.Items["tag"] = tag;
        return ctx;
    }

    private static async Task<(int Status, string Body, string CacheHeader, string ServedBy)> InvokeAsync(
        RedisCacheMiddleware middleware,
        string method,
        string path,
        string accept)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.Accept = accept;
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync();
        return (
            ctx.Response.StatusCode,
            body,
            ctx.Response.Headers["X-Cache"].ToString(),
            ctx.Response.Headers["X-Served-By"].ToString());
    }

    private sealed class InMemoryCacheStore : ICacheStore
    {
        private readonly ConcurrentDictionary<string, (CachedGatewayResponse Value, DateTime ExpiresAt)> _map = new();

        public Task<(CachedGatewayResponse? Entry, int? TtlSeconds)> GetAsync(string key, CancellationToken ct)
        {
            if (_map.TryGetValue(key, out var item) && item.ExpiresAt > DateTime.UtcNow)
            {
                int ttl = Math.Max(1, (int)(item.ExpiresAt - DateTime.UtcNow).TotalSeconds);
                return Task.FromResult<(CachedGatewayResponse?, int?)>((item.Value, ttl));
            }
            return Task.FromResult<(CachedGatewayResponse?, int?)>((null, null));
        }

        public Task SetAsync(string key, CachedGatewayResponse value, TimeSpan ttl, CancellationToken ct)
        {
            _map[key] = (value, DateTime.UtcNow.Add(ttl));
            return Task.CompletedTask;
        }

        public Task InvalidateByPrefixAsync(string prefix, CancellationToken ct)
        {
            foreach (var k in _map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                _map.TryRemove(k, out _);
            return Task.CompletedTask;
        }
    }
}
