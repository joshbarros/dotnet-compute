using System.Text.Json;
using Compute.Api.Data;
using Compute.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compute.Api.Modules.Reservations;

[ApiController]
[Route("api/reservations")]
public class ReservationsController(ComputeDbContext db, ReservationService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the reservation. The hour hold and the lease follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<ReservationDto>> Create(CreateReservationRequest request, CancellationToken ct)
    {
        var reservation = await service.CreateAsync(request, ct);
        return Accepted($"/api/reservations/{reservation.Id}", ReservationDto.From(reservation));
    }

    [HttpGet]
    public async Task<IEnumerable<ReservationDto>> List([FromQuery] string? poolCode, CancellationToken ct)
    {
        var query = db.Reservations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(poolCode))
            query = query.Where(b => b.PoolCode == poolCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(ReservationDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReservationDto>> Get(Guid id, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return reservation is null ? NotFound() : ReservationDto.From(reservation);
    }

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<ReservationDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/reservations/{id}", ReservationDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one reservation, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
