using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Ocelot.Provider.Consul;
using Ocelot.Provider.Polly;
using StackExchange.Redis;
using Warehouse.Gateway;

var builder = WebApplication.CreateBuilder(args);

string consulHost = Environment.GetEnvironmentVariable("CONSUL_HOST") ?? "consul";
int consulPort = int.TryParse(Environment.GetEnvironmentVariable("CONSUL_PORT"), out int cp) ? cp : 8500;
string redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "redis:6379";
int cacheTtl = int.TryParse(Environment.GetEnvironmentVariable("CACHE_TTL_SECONDS"), out int ttl) ? ttl : 30;
int maxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("GATEWAY_MAX_CONCURRENT"), out int mc) ? mc : 2;

builder.Configuration
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: false)
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["GlobalConfiguration:ServiceDiscoveryProvider:Host"] = consulHost,
        ["GlobalConfiguration:ServiceDiscoveryProvider:Port"] = consulPort.ToString()
    });

builder.Services.AddSingleton(new PriorityOptions { MaxConcurrent = maxConcurrent });
builder.Services.AddSingleton(new CacheOptions { TtlSeconds = cacheTtl });
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var opts = ConfigurationOptions.Parse(redisConn);
    opts.AbortOnConnectFail = false;
    opts.ConnectTimeout = 2000;
    return ConnectionMultiplexer.Connect(opts);
});
builder.Services.AddSingleton<ICacheStore, RedisCacheStore>();

builder.Services
    .AddOcelot(builder.Configuration)
    .AddConsul<CustomConsulServiceBuilder>()
    .AddPolly();

var app = builder.Build();

app.MapWhen(
    ctx => ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase),
    healthApp => healthApp.Run(async ctx =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"status\":\"healthy\",\"service\":\"gateway\"}");
    }));

app.UseMiddleware<PriorityQueueMiddleware>();
app.UseMiddleware<RedisCacheMiddleware>();

await app.UseOcelot();
app.Run();
