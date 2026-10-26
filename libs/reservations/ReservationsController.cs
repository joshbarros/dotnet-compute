using System.Text.Json;
using Compute.Shared.Auth;
using Compute.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Compute.Reservations;

[ApiController]
[Route("api/reservations")]
[Authorize]
public class ReservationsController(ReservationService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the reservation for the signed-in user. Pools and the lease follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<ReservationDto>> Create(CreateReservationRequest request, CancellationToken ct)
    {
        var reservation = await service.CreateAsync(request, ct);
        return Accepted($"/api/reservations/{reservation.Id}", ReservationDto.From(reservation));
    }

    /// <summary>Your reservations. Staff see everyone's.</summary>
    [HttpGet]
    public async Task<IEnumerable<ReservationDto>> List([FromQuery] string? poolCode, CancellationToken ct) =>
        (await service.ListAsync(poolCode, ct)).Select(ReservationDto.From);

    [HttpGet("{id:guid}")]
    public async Task<ReservationDto> Get(Guid id, CancellationToken ct) => ReservationDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<ReservationDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/reservations/{id}", ReservationDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one reservation, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
