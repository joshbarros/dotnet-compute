using Compute.Shared;
using Compute.Shared.Contracts;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Compute.Pools;

public static class PoolsModule
{
    public static IServiceCollection AddPoolsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Compute") ?? throw new InvalidOperationException("Connection string 'Compute' is not configured.");
        services.AddDbContext<PoolsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PoolsDbContext.SchemaName)));
        services.AddScoped<PoolsHandler>();
        services.AddScoped<IPoolCatalog, PoolCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<PoolsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<PoolsDbContext>>("hours-db", tags: ["ready"]);

        messaging.AddOutbox<PoolsDbContext>()
            .AddConsumer<PoolsHandler>(Topology.PoolsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
