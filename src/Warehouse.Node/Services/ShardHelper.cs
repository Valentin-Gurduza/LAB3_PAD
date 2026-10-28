using System.Text;

namespace Warehouse.Node.Services;

public static class ShardHelper
{
    public static int GetShard(string id, int shardCount = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(shardCount, 0);

        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(id ?? string.Empty))
        {
            hash ^= b;
            hash *= prime;
        }

        return (int)(hash % (uint)shardCount);
    }
}
