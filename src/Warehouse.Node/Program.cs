using Consul;
using MongoDB.Driver;
using Warehouse.Node.Models;
using Warehouse.Node.Services;

var builder = WebApplication.CreateBuilder(args);

var nodeOptions = new NodeOptions
{
    NodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? builder.Configuration["Node:NodeId"] ?? "api-1",
    ShardId = int.TryParse(Environment.GetEnvironmentVariable("SHARD_ID") ?? builder.Configuration["Node:ShardId"], out int sId) ? sId : 0,
    ShardCount = int.TryParse(Environment.GetEnvironmentVariable("SHARD_COUNT") ?? builder.Configuration["Node:ShardCount"], out int sCnt) ? sCnt : 3,
    ServiceHost = Environment.GetEnvironmentVariable("SERVICE_HOST") ?? builder.Configuration["Node:ServiceHost"] ?? "localhost",
    ServicePort = int.TryParse(Environment.GetEnvironmentVariable("SERVICE_PORT") ?? builder.Configuration["Node:ServicePort"], out int sPort) ? sPort : 8080,
    MongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION") ?? builder.Configuration["Node:MongoConnectionString"] ?? "mongodb://localhost:27017",
    MongoDatabase = Environment.GetEnvironmentVariable("MONGO_DATABASE") ?? builder.Configuration["Node:MongoDatabase"] ?? "warehouse",
    ConsulAddress = Environment.GetEnvironmentVariable("CONSUL_ADDRESS") ?? builder.Configuration["Node:ConsulAddress"] ?? "http://localhost:8500",
    EnableConsul = !bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_CONSUL") ?? builder.Configuration["Node:EnableConsul"], out bool enConsul) || enConsul
};

builder.Services.AddSingleton(nodeOptions);
builder.Services.AddHttpClient("peer");
builder.Services.AddSingleton<IConsulClient>(_ => new ConsulClient(cfg => cfg.Address = new Uri(nodeOptions.ConsulAddress)));
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(nodeOptions.MongoConnectionString));
builder.Services.AddSingleton<IWarehouseRepository, MongoWarehouseRepository>();
builder.Services.AddSingleton<IPeerShardClient, PeerShardClient>();
builder.Services.AddHostedService<ConsulRegistrationHostedService>();

builder.Services
    .AddControllers()
    .AddXmlSerializerFormatters();

var app = builder.Build();

app.MapGet("/health", async (IWarehouseRepository repo, NodeOptions opts, CancellationToken ct) =>
{
    bool dbOk = await repo.PingAsync(ct);
    return dbOk
        ? Results.Ok(new { status = "healthy", node = opts.NodeId, shard = opts.ShardId })
        : Results.Json(new { status = "unhealthy", node = opts.NodeId, shard = opts.ShardId }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapControllers();
app.Run();
