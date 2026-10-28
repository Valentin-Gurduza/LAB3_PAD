namespace Warehouse.Node.Models;

public class NodeOptions
{
    public string NodeId { get; set; } = "api-1";
    public int ShardId { get; set; } = 0;
    public int ShardCount { get; set; } = 3;
    public string ServiceHost { get; set; } = "localhost";
    public int ServicePort { get; set; } = 8080;
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";
    public string MongoDatabase { get; set; } = "warehouse";
    public string ConsulAddress { get; set; } = "http://localhost:8500";
    public bool EnableConsul { get; set; } = true;
}
