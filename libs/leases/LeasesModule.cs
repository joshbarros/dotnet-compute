using Compute.Shared;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compute.Leases;

public static class LeasesModule
{
    public static IServiceCollection AddLeasesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Compute") ?? throw new InvalidOperationException("Connection string 'Compute' is not configured.");
        services.AddDbContext<LeasesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", LeasesDbContext.SchemaName)));
        services.AddScoped<LeasesHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<LeasesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<LeasesDbContext>>("leases-db", tags: ["ready"]);

        messaging.AddOutbox<LeasesDbContext>()
            .AddConsumer<LeasesHandler>(Topology.LeasesQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
