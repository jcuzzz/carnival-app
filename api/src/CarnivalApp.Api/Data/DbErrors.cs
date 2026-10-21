using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CarnivalApp.Api.Data;

/// <summary>
/// Identifies a constraint failure by the provider's code rather than by re-reading: a re-read
/// cannot tell whether the competing row survived the savepoint rollback.
/// </summary>
public static class DbErrors
{
    private const int SqliteUniqueViolation = 2067;
    private const int SqliteForeignKeyViolation = 787;

    public static bool IsUniqueViolation(DbUpdateException ex, string table, string column) =>
        ex.InnerException switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg =>
                pg.ConstraintName == $"IX_{table}_{column}",
            SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation } lite =>
                lite.Message.Contains($"{table}.{column}", StringComparison.Ordinal),
            _ => false,
        };

    public static bool IsForeignKeyViolation(DbUpdateException ex) => ex.InnerException
        is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }
        or SqliteException { SqliteExtendedErrorCode: SqliteForeignKeyViolation };
}
