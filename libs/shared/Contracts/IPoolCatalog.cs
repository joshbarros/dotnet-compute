namespace Compute.Shared.Contracts;

public record PoolInfo(Guid Id, string Code);

/// <summary>
/// What Reservations may ask Pools, without referencing it. Pools implements it over its own tables;
/// Reservations never reads another module's schema.
/// </summary>
public interface IPoolCatalog
{
    Task<PoolInfo?> FindAsync(string code, CancellationToken ct);
}
