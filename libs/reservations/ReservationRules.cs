namespace Compute.Reservations;

public static class ReservationRules
{
    public const int MaxHoursPerReservation = 24;
    public const int MaxReferenceLength = 40;
    public const long RateCentsPerGpuHour = 320;
    public const long MinimumChargeCents = 1_500;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long hours)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Team reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Team reference must be at most {MaxReferenceLength} characters.";
        if (hours < 1)
            return "A reservation needs at least 1 hour.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (hours > MaxHoursPerReservation)
            return $"A reservation takes at most {MaxHoursPerReservation} hours.";
        return null;
    }

    /// <summary>The per-GPU-hour rate with a minimum charge, in cents. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(int hours) => checked(Math.Max(MinimumChargeCents, hours * RateCentsPerGpuHour));
}
