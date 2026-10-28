using System.ComponentModel.DataAnnotations;
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

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
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
