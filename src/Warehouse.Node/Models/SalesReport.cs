namespace Warehouse.Node.Models;

public class SalesReport
{
    public int TotalRecords { get; set; }

    public decimal GrandTotalRevenue { get; set; }

    public List<int> ActiveShards { get; set; } = [];

    public List<GroupMetric> ByRegion { get; set; } = [];

    public List<GroupMetric> ByCategory { get; set; } = [];

    public static SalesReport FromRecords(IEnumerable<SaleRecord> records, IEnumerable<int> activeShards)
    {
        var list = records.ToList();
        return new SalesReport
        {
            TotalRecords = list.Count,
            GrandTotalRevenue = list.Sum(r => r.TotalAmount),
            ActiveShards = activeShards.Distinct().OrderBy(s => s).ToList(),
            ByRegion = list
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Region) ? "Unknown" : r.Region)
                .OrderBy(g => g.Key)
                .Select(g => new GroupMetric
                {
                    Name = g.Key,
                    Count = g.Count(),
                    TotalQuantity = g.Sum(x => x.Quantity),
                    TotalRevenue = g.Sum(x => x.TotalAmount)
                })
                .ToList(),
            ByCategory = list
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Unknown" : r.Category)
                .OrderBy(g => g.Key)
                .Select(g => new GroupMetric
                {
                    Name = g.Key,
                    Count = g.Count(),
                    TotalQuantity = g.Sum(x => x.Quantity),
                    TotalRevenue = g.Sum(x => x.TotalAmount)
                })
                .ToList()
        };
    }
}

public class GroupMetric
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public int TotalQuantity { get; set; }

    public decimal TotalRevenue { get; set; }
}
