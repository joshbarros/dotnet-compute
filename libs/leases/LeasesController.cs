using Compute.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compute.Leases;

public record LeaseDto(Guid ReservationId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/leases")]
[Authorize]
public class LeasesController(LeasesDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{reservationId:guid}")]
    public async Task<ActionResult<LeaseDto>> Get(Guid reservationId, CancellationToken ct)
    {
        var doc = await db.Leases.AsNoTracking().FirstOrDefaultAsync(d => d.ReservationId == reservationId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new LeaseDto(doc.ReservationId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
