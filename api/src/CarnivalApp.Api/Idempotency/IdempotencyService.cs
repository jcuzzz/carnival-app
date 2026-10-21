using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Idempotency;

public sealed record IdempotencyOutcome(int StatusCode, string Body, bool Replayed);

/// <summary>
/// Only a 2xx is recorded, in the same transaction as the mutation.
/// Any other outcome rolls back and leaves the key unburned: refusals
/// are state-dependent, and a burnt key would strand queued work.
/// </summary>
public class IdempotencyService(SportsDbContext db)
{
    public const string HeaderName = "Idempotency-Key";

    public static string HashPayload(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    public static string HashPayload(string payload) =>
        HashPayload(Encoding.UTF8.GetBytes(payload));

    /// <summary>
    /// The action must mutate through the shared DbContext
    /// and must not commit transactions of its own.
    /// </summary>
    public async Task<IdempotencyOutcome> ExecuteAsync(
        Guid key,
        string payloadHash,
        string actorSubject,
        string? deviceId,
        Func<CancellationToken, Task<(int StatusCode, string Body)>> action,
        CancellationToken ct = default)
    {
        var existing = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);
        if (existing is not null)
            return Replay(existing, payloadHash);

        // No execution strategy is configured. If retries are ever enabled, clear the ChangeTracker
        // and re-read the endpoint's state per attempt, or the retry double-writes.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (status, body) = await action(ct);

            if (status is < 200 or > 299)
            {
                await tx.RollbackAsync(ct);
                return new IdempotencyOutcome(status, body, Replayed: false);
            }

            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Key = key,
                PayloadHash = payloadHash,
                ResponseStatusCode = status,
                ResponseBody = body,
                ActorSubject = actorSubject,
                DeviceId = deviceId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new IdempotencyOutcome(status, body, Replayed: false);
        }
        catch (DbUpdateException)
        {
            // Lost a concurrent race on the key: another request committed first.
            db.ChangeTracker.Clear();
            var winner = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key, ct);
            // No winner: the violation came from the action's own mutation, not the key insert.
            if (winner is null) throw;
            return Replay(winner, payloadHash);
        }
    }

    private static readonly string PayloadMismatchBody = System.Text.Json.JsonSerializer.Serialize(
        ApiError.Body(
            ErrorCodes.IdempotencyPayloadMismatch,
            "This idempotency key was already used with a different payload. " +
            "Keys are single-use per submission; generate a new key for a new submission."),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private static IdempotencyOutcome Replay(IdempotencyKey stored, string payloadHash) =>
        stored.PayloadHash == payloadHash
            ? new IdempotencyOutcome(stored.ResponseStatusCode, stored.ResponseBody, Replayed: true)
            : new IdempotencyOutcome(
                StatusCodes.Status422UnprocessableEntity,
                PayloadMismatchBody,
                Replayed: false);
}
