namespace CarnivalApp.Api.Http;

/// <summary>
/// Column sizes shared with Data/Configurations. SQLite ignores HasMaxLength, so an over-long
/// value otherwise reaches PostgreSQL as 22001 and a retryable 500 that blocks the drain.
/// </summary>
public static class FieldLimits
{
    public const int Reason = 1000;
    public const int Code = 64;
    public const int Sport = 64;
    public const int Name = 200;
    public const int Units = 16;
    public const int HouseCode = 32;
    public const int Category = 64;
    public const int Subject = 128;
    public const int DisplayName = 200;

    // Measured trimmed, because that is what is stored.
    public static IResult? Refuse(string? value, int limit, string field) =>
        value is not null && value.Trim().Length > limit
            ? ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"{field} is limited to {limit} characters.")
            : null;
}
