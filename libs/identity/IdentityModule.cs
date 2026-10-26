using System.Threading.RateLimiting;
using Compute.Shared;
using Compute.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Compute.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Compute") ?? throw new InvalidOperationException("Connection string 'Compute' is not configured.");

        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
            .Validate(JwtOptions.IsValid, "Jwt:Key must be at least 32 bytes, AccessMinutes 1-120 and RefreshDays 1-90.")
            .ValidateOnStart();
        services.Configure<SeedAdminOptions>(config.GetSection(SeedAdminOptions.Section));

        services.AddDbContext<IdentityModuleDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", IdentityModuleDbContext.Schema)));

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 4;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IdentityModuleDbContext>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((o, tokens) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = tokens.ValidationParameters();
            });

        var authPerMinute = config.GetValue("RateLimit:AuthPerMinute", 20);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        services.AddSingleton<IModuleMigrator>(new EfMigrator<IdentityModuleDbContext>(order: 0));
        services.AddSingleton<IModuleMigrator, IdentitySeeder>();
        services.AddHealthChecks().AddCheck<DbReadinessCheck<IdentityModuleDbContext>>("identity-db", tags: ["ready"]);
        return services;
    }
}

/// <summary>Makes sure the three roles exist and, if configured, that the first administrator does.</summary>
public sealed class IdentitySeeder(IOptions<SeedAdminOptions> seed, ILogger<IdentitySeeder> logger) : IModuleMigrator
{
    public int Order => 1;

    public async Task MigrateAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in new[] { Roles.Admin, Roles.Staff, Roles.Customer })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.CreateVersion7() });

        var o = seed.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrEmpty(o.Password))
        {
            if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count == 0)
                logger.LogWarning("No administrator exists and Identity:SeedAdmin is not configured. Nobody can manage the catalog.");
            return;
        }

        if (await users.FindByEmailAsync(o.Email) is not null)
            return;

        var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = o.Email, Email = o.Email, EmailConfirmed = true };
        var created = await users.CreateAsync(admin, o.Password);
        if (!created.Succeeded)
            throw new InvalidOperationException("The seeded administrator could not be created: " + string.Join(" ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(admin, Roles.Admin);
        logger.LogInformation("Seeded administrator {Email}", o.Email);
    }
}
