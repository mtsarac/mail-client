using Microsoft.EntityFrameworkCore;

// Maps Postgres unique violations to named constraints for race-safe conflict handling.
namespace MailClient.Infrastructure.Persistence;

// Race-safe unique-constraint recognition for PostgreSQL (SQLSTATE 23505).
// The database constraint remains the final authority; application-level
// pre-checks are friendly but cannot close the check-then-insert race.
public static class DbUniqueViolation
{
    private const string UniqueViolationSqlState = "23505";

    public static bool IsUniqueViolationFor(DbUpdateException exception, string constraintFragment)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            // Match Npgsql's PostgresException by shape (no hard package
            // reference required): SqlState 23505 + expected constraint name.
            if (string.Equals(inner.GetType().Name, "PostgresException", StringComparison.Ordinal)
                && string.Equals(GetProperty(inner, "SqlState") as string, UniqueViolationSqlState, StringComparison.Ordinal)
                && GetProperty(inner, "ConstraintName") is string constraint
                && constraint.Contains(constraintFragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static object? GetProperty(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);
}
