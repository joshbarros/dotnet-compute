using System.Net.Mail;
using Compute.Shared;
using Compute.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compute.Identity;

public record UserInfo(Guid Id, string Email, string[] Roles);
public record AuthResult(AccessToken Access, string RefreshToken, DateTimeOffset RefreshExpiresAt, UserInfo User);

public sealed class AuthService(
    UserManager<ApplicationUser> users,
    IdentityModuleDbContext db,
    TokenService tokens,
    IOptions<JwtOptions> options,
    TimeProvider clock)
{
    private readonly JwtOptions _o = options.Value;

    public async Task<AuthResult> RegisterAsync(string? email, string? password, CancellationToken ct)
    {
        if (!IsEmail(email))
            throw new DomainException(400, "A valid email address is required.");
        if (string.IsNullOrEmpty(password))
            throw new DomainException(400, "A password is required.");

        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new DomainException(created.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail") ? 409 : 400,
                string.Join(" ", created.Errors.Select(e => e.Description)));

        await users.AddToRoleAsync(user, Roles.Customer);
        return await IssueAsync(user, Guid.CreateVersion7(), ct);
    }

    public async Task<AuthResult> LoginAsync(string? email, string? password, CancellationToken ct)
    {
        var invalid = new DomainException(401, "Invalid email or password.");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw invalid;

        var user = await users.FindByEmailAsync(email);
        if (user is null)
            throw invalid;
        if (await users.IsLockedOutAsync(user))
            throw new DomainException(429, "Too many failed attempts. Try again in 15 minutes.");
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            throw await users.IsLockedOutAsync(user)
                ? new DomainException(429, "Too many failed attempts. Try again in 15 minutes.")
                : invalid;
        }

        await users.ResetAccessFailedCountAsync(user);
        return await IssueAsync(user, Guid.CreateVersion7(), ct);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair. The old token is revoked with one conditional UPDATE, so two requests
    /// presenting the same token cannot both succeed. A token that was already used means it leaked: revoke the whole family.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string? rawToken, CancellationToken ct)
    {
        var unauthorised = new DomainException(401, "The session has expired. Sign in again.");
        if (string.IsNullOrEmpty(rawToken))
            throw unauthorised;

        var hash = TokenService.Hash(rawToken);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw unauthorised;
        var now = clock.GetUtcNow();

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw unauthorised;
        }
        if (stored.ExpiresAt <= now)
            throw unauthorised;

        var user = await users.FindByIdAsync(stored.UserId.ToString()) ?? throw unauthorised;
        if (await users.IsLockedOutAsync(user))
            throw unauthorised;

        var next = TokenService.NewRefreshToken();
        var nextHash = TokenService.Hash(next);
        var revoked = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedByHash, nextHash), ct);
        if (revoked != 1)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw unauthorised;
        }

        return await IssueAsync(user, stored.FamilyId, ct, next);
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
            return;
        var hash = TokenService.Hash(rawToken);
        var familyId = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (familyId is { } id)
            await RevokeFamilyAsync(id, ct);
    }

    private async Task<AuthResult> IssueAsync(ApplicationUser user, Guid familyId, CancellationToken ct, string? refreshToken = null)
    {
        var roles = (await users.GetRolesAsync(user)).ToArray();
        var access = tokens.CreateAccessToken(user.Id, user.Email!, roles);

        var raw = refreshToken ?? TokenService.NewRefreshToken();
        var now = clock.GetUtcNow();
        var expires = now.AddDays(_o.RefreshDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = TokenService.Hash(raw),
            CreatedAt = now,
            ExpiresAt = expires,
        });
        await db.SaveChangesAsync(ct);
        return new AuthResult(access, raw, expires, new UserInfo(user.Id, user.Email!, roles));
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && MailAddress.TryCreate(value, out var m) && m.Address == value;
}
