using Compute.Shared;
using Compute.Shared.Auth;
using Compute.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compute.Pools;

public record CreatePoolRequest(string? Code, string? Name, string? GpuModel, string? Region, DateTimeOffset? Day, long HourCapacity);

public record PoolDto(Guid Id, string Code, string Name, string GpuModel, string Region, DateTimeOffset Day, int HourCapacity, int HoursLeft)
{
    public static PoolDto From(Pool s) => new(s.Id, s.Code, s.Name, s.GpuModel, s.Region, s.Day, s.HourCapacity, s.HoursLeft);
}

/// <summary>What other modules may ask Pools, over Pools's own tables.</summary>
public sealed class PoolCatalog(PoolsDbContext db) : IPoolCatalog
{
    public async Task<PoolInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Pools.AsNoTracking().Where(s => s.Code == code).Select(s => new PoolInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/pools")]
[Authorize]
public class PoolsController(PoolsDbContext db) : ControllerBase
{
    private const int MaxHourCapacity = 500;

    [HttpGet]
    public async Task<IEnumerable<PoolDto>> List(CancellationToken ct) =>
        (await db.Pools.AsNoTracking().OrderBy(s => s.Day).ThenBy(s => s.Code).ToListAsync(ct)).Select(PoolDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<PoolDto>> Get(string code, CancellationToken ct)
    {
        var pool = await db.Pools.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return pool is null ? NotFound() : PoolDto.From(pool);
    }

    /// <summary>Opens a pool. Staff only. This is the only place HoursLeft is initialised; after that only the Pools handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<PoolDto>> Create(CreatePoolRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.GpuModel) || string.IsNullOrWhiteSpace(request.Region))
            throw new DomainException(400, "Name, gpuModel and region are required.");
        if (request.HourCapacity < 1 || request.HourCapacity > MaxHourCapacity)
            throw new DomainException(400, $"Hour capacity must be between 1 and {MaxHourCapacity} hours.");
        if (await db.Pools.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Pool '{code}' already exists.");

        var pool = new Pool
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            GpuModel = request.GpuModel.Trim(),
            Region = request.Region.Trim(),
            Day = request.Day ?? DateTimeOffset.UtcNow.AddDays(14),
            HourCapacity = (int)request.HourCapacity,
            HoursLeft = (int)request.HourCapacity,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(pool);
        await db.SaveChangesAsync(ct);
        return Created($"/api/pools/{pool.Code}", PoolDto.From(pool));
    }
}
