using Compute.Shared;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compute.Reservations;

public static class ReservationsModule
{
    public static IServiceCollection AddReservationsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Compute") ?? throw new InvalidOperationException("Connection string 'Compute' is not configured.");
        services.AddDbContext<ReservationsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ReservationsDbContext.SchemaName)));
        services.Configure<ReservationOptions>(config.GetSection("Reservations"));
        services.AddScoped<ReservationService>();
        services.AddScoped<ReservationsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ReservationsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ReservationsDbContext>>("reservations-db", tags: ["ready"]);

        messaging.AddOutbox<ReservationsDbContext>()
            .AddConsumer<ReservationsHandler>(Topology.ReservationsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
