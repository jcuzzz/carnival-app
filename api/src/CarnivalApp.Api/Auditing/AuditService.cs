using System.Text.Json;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Auditing;

public sealed record AuditActor(string Subject, string? Role);

/// <summary>
/// Never calls SaveChanges: the row rides the caller's own save, so it commits or rolls back
/// atomically with the mutation it records.
/// </summary>
public class AuditService(CarnivalAppDbContext db, IConfiguration configuration)
{
    private static readonly string AppVersion =
        typeof(AuditService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public AuditEvent Record(
        AuditActor actor,
        string action,
        string entityType,
        string entityId,
        object? priorValues = null,
        object? newValues = null,
        string? reason = null,
        Guid? importBatchId = null,
        string? ruleVersion = null)
    {
        var audit = new AuditEvent
        {
            ActorSubject = actor.Subject,
            ActorRole = actor.Role,
            AtUtc = DateTimeOffset.UtcNow,
            SchoolTimezone = School.SchoolTimezone.From(configuration),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            PriorValuesJson = priorValues is null ? null : JsonSerializer.Serialize(priorValues, JsonOptions),
            NewValuesJson = newValues is null ? null : JsonSerializer.Serialize(newValues, JsonOptions),
            Reason = reason,
            ImportBatchId = importBatchId,
            ApplicationVersion = AppVersion,
            RuleVersion = ruleVersion,
        };
        db.AuditEvents.Add(audit);
        return audit;
    }
}
