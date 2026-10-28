using Warehouse.Node.Models;

namespace Warehouse.Node.Services;

public interface IWarehouseRepository
{
    Task<List<SaleRecord>> GetAllAsync(string? region, string? category, CancellationToken ct);
    Task<SaleRecord?> GetByIdAsync(string id, CancellationToken ct);
    Task UpsertAsync(SaleRecord record, CancellationToken ct);
    Task<bool> DeleteAsync(string id, CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);
}
