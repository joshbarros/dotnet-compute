using Compute.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compute.Api.Modules.Leases;

public record LeaseDto(Guid ReservationId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/leases")]
public class LeasesController(ComputeDbContext db) : ControllerBase
{
    [HttpGet("{reservationId:guid}")]
    public async Task<ActionResult<LeaseDto>> Get(Guid reservationId, CancellationToken ct)
    {
        var doc = await db.Leases.AsNoTracking().FirstOrDefaultAsync(d => d.ReservationId == reservationId, ct);
        return doc is null
            ? NotFound()
            : new LeaseDto(doc.ReservationId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
