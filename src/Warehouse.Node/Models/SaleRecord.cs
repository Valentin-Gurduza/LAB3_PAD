using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Warehouse.Node.Models;

public class SaleRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Product { get; set; } = string.Empty;

    public int Quantity { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal UnitPrice { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal TotalAmount { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public int ShardId { get; set; }

    public void Recalculate()
    {
        TotalAmount = Quantity * UnitPrice;
    }
}
