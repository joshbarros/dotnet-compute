using System.Text;

namespace Compute.Identity;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "compute-api";
    public string Audience { get; set; } = "compute-web";

    /// <summary>HMAC-SHA256 signing key. At least 32 bytes. Rotate it to sign everyone out.</summary>
    public string Key { get; set; } = "";

    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 14;

    /// <summary>Set true behind TLS so the refresh cookie is only ever sent over HTTPS.</summary>
    public bool SecureCookies { get; set; }

    public static bool IsValid(JwtOptions o) =>
        Encoding.UTF8.GetByteCount(o.Key) >= 32 && o.AccessMinutes is >= 1 and <= 120 && o.RefreshDays is >= 1 and <= 90;
}

public class SeedAdminOptions
{
    public const string Section = "Identity:SeedAdmin";

    public string? Email { get; set; }
    public string? Password { get; set; }
}
