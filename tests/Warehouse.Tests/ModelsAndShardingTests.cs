using System.Text.Json;
using System.Xml.Serialization;
using Warehouse.Node.Models;
using Warehouse.Node.Services;
using Xunit;

namespace Warehouse.Tests;

public class ModelsAndShardingTests
{
    [Fact]
    public void GetShard_IsDeterministicAndHandlesEdgeInputs()
    {
        Assert.Equal(ShardHelper.GetShard("sale-1001", 3), ShardHelper.GetShard("sale-1001", 3));
        Assert.InRange(ShardHelper.GetShard("", 3), 0, 2);
        Assert.InRange(ShardHelper.GetShard("șțâîă-🔥-999999", 3), 0, 2);

        var shards = Enumerable.Range(1, 30)
            .Select(i => ShardHelper.GetShard($"sale-{i}", 3))
            .Distinct()
            .ToList();
        Assert.Equal(3, shards.Count);
    }

    [Fact]
    public void SaleRecordAndSalesReport_RoundTripXmlAndJson()
    {
        var record = new SaleRecord
        {
            Id = "sale-1",
            Region = "EU",
            Category = "Tech",
            Product = "Laptop",
            Quantity = 2,
            UnitPrice = 1500m,
            RecordedAt = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc),
            ShardId = 1
        };
        record.Recalculate();
        Assert.Equal(3000m, record.TotalAmount);

        var records = new List<SaleRecord> { record };
        var report = SalesReport.FromRecords(records, [0, 1, 2]);

        var listXml = RoundTripXml(records);
        Assert.Single(listXml);
        Assert.Equal("sale-1", listXml[0].Id);
        Assert.Equal(3000m, listXml[0].TotalAmount);

        var reportXml = RoundTripXml(report);
        Assert.Equal(1, reportXml.TotalRecords);
        Assert.Equal(3000m, reportXml.GrandTotalRevenue);
        Assert.Equal([0, 1, 2], reportXml.ActiveShards);
        Assert.Equal("EU", Assert.Single(reportXml.ByRegion).Name);
        Assert.Equal("Tech", Assert.Single(reportXml.ByCategory).Name);

        var listJson = JsonSerializer.Deserialize<List<SaleRecord>>(JsonSerializer.Serialize(records))!;
        Assert.Equal(3000m, Assert.Single(listJson).TotalAmount);

        var reportJson = JsonSerializer.Deserialize<SalesReport>(JsonSerializer.Serialize(report))!;
        Assert.Equal(3000m, reportJson.GrandTotalRevenue);
    }

    private static T RoundTripXml<T>(T value)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var writer = new StringWriter();
        serializer.Serialize(writer, value);
        using var reader = new StringReader(writer.ToString());
        return (T)serializer.Deserialize(reader)!;
    }
}
