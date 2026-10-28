using MongoDB.Bson;
using MongoDB.Driver;
using Warehouse.Node.Models;

namespace Warehouse.Node.Services;

public class MongoWarehouseRepository : IWarehouseRepository
{
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<SaleRecord> _collection;

    public MongoWarehouseRepository(IMongoClient client, NodeOptions options)
    {
        _db = client.GetDatabase(options.MongoDatabase);
        _collection = _db.GetCollection<SaleRecord>("sales");
    }

    public async Task<List<SaleRecord>> GetAllAsync(string? region, string? category, CancellationToken ct)
    {
        var filter = Builders<SaleRecord>.Filter.Empty;
        if (!string.IsNullOrWhiteSpace(region))
            filter &= Builders<SaleRecord>.Filter.Eq(x => x.Region, region);
        if (!string.IsNullOrWhiteSpace(category))
            filter &= Builders<SaleRecord>.Filter.Eq(x => x.Category, category);

        return await _collection.Find(filter).SortBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<SaleRecord?> GetByIdAsync(string id, CancellationToken ct) =>
        await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(SaleRecord record, CancellationToken ct) =>
        await _collection.ReplaceOneAsync(
            x => x.Id == record.Id,
            record,
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var res = await _collection.DeleteOneAsync(x => x.Id == id, ct);
        return res.DeletedCount > 0;
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
