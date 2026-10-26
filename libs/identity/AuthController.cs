using Compute.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Compute.Identity;

public record CredentialsRequest(string? Email, string? Password);

public record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, UserInfo User);

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController(AuthService auth, IOptions<JwtOptions> options) : ControllerBase
{
    public const string RefreshCookie = "compute_refresh";
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "compute-web";

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register(CredentialsRequest request, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(request.Email, request.Password, ct);
        SetCookie(result);
        return StatusCode(StatusCodes.Status201Created, ToResponse(result));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(CredentialsRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, ct);
        SetCookie(result);
        return ToResponse(result);
    }

    /// <summary>The refresh token travels in an HttpOnly cookie. A custom header is required so another site cannot make the browser call this.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<TokenResponse>> Refresh(CancellationToken ct)
    {
        RequireSameOriginHeader();
        var result = await auth.RefreshAsync(Request.Cookies[RefreshCookie], ct);
        SetCookie(result);
        return ToResponse(result);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        RequireSameOriginHeader();
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<UserInfo> Me() => new UserInfo(
        Guid.Parse(User.FindFirst("sub")!.Value),
        User.FindFirst("email")?.Value ?? "",
        User.FindAll("role").Select(c => c.Value).ToArray());

    private void RequireSameOriginHeader()
    {
        if (Request.Headers[RequestedWithHeader] != RequestedWithValue)
            throw new DomainException(400, $"The {RequestedWithHeader} header is required.");
    }

    private void SetCookie(AuthResult result) =>
        Response.Cookies.Append(RefreshCookie, result.RefreshToken, CookieOptions(result.RefreshExpiresAt));

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.Value.SecureCookies || Request.IsHttps,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };

    private static TokenResponse ToResponse(AuthResult r) => new(r.Access.Value, r.Access.ExpiresAt, r.User);
}
