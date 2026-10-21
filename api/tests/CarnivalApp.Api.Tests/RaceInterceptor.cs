using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Deterministic stand-in for a competing request, for the optimistic-concurrency
/// tests (assessment C4). Armed with a marker, it fires ONCE immediately before
/// the next database command whose text contains that marker — so a test can
/// place another writer at an exact point inside a handler (between the
/// finalisation gate's check and its save, or just before a status UPDATE)
/// instead of hoping a real race lands in the right microsecond.
///
/// The competing write runs on the connection and transaction EF is already
/// using: the HTTP tests share one in-memory SQLite connection, which cannot
/// host a genuinely parallel second writer. What that costs is durability — a
/// competing write inside a transaction that then rolls back disappears with it
/// — so tests assert the refusal and the ABSENCE of the losing change, and use
/// two DbContexts (<see cref="LifecycleConcurrencyTests"/>) where a surviving
/// winner is the point.
/// </summary>
public sealed class RaceInterceptor : DbCommandInterceptor
{
    private string? _marker;
    private Action<DbTransaction?>? _write;
    private string? _countMarker;

    /// <summary>Count commands containing <paramref name="marker"/> from now on.</summary>
    public void CountFrom(string marker)
    {
        _countMarker = marker;
        Counted = 0;
    }

    public int Counted { get; private set; }

    /// <summary>Run <paramref name="write"/> once, just before the next command containing <paramref name="marker"/>.</summary>
    public void ArmOnce(string marker, Action<DbTransaction?> write)
    {
        _marker = marker;
        _write = write;
    }

    public void Disarm()
    {
        _marker = null;
        _write = null;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Fire(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Fire(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Fire(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Fire(command);
        return ValueTask.FromResult(result);
    }

    private void Fire(DbCommand command)
    {
        if (_countMarker is not null && command.CommandText.Contains(_countMarker, StringComparison.Ordinal))
            Counted++;
        if (_marker is null || _write is null) return;
        if (!command.CommandText.Contains(_marker, StringComparison.Ordinal)) return;

        var write = _write;
        Disarm(); // once, and before invoking, so the competing write cannot re-enter
        write(command.Transaction);
    }
}
