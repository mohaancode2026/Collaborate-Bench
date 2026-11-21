using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Infrastructure;

/// <summary>
/// Turns SQL Server / EF Core failures into messages a recruiter can act on. Pure mapping, no logging:
/// <see cref="GlobalExceptionHandler"/> logs each failure once, with the SQL error number.
/// </summary>
public static class SqlErrors
{
    /// <summary>The first <see cref="SqlException"/> in the exception chain (EF wraps them in DbUpdateException etc.).</summary>
    public static SqlException? Find(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SqlException s) return s;
        return null;
    }

    /// <summary>True for a unique key / unique index violation. Use it to turn a race into a friendly "already exists" message.</summary>
    public static bool IsDuplicate(Exception ex) => Find(ex)?.Number is 2627 or 2601;

    // Errors raised while connecting (server down, wrong name, login failed, Azure SQL busy).
    private static readonly HashSet<int> ConnectionErrors = [-1, 2, 53, 233, 4060, 10053, 10054, 10060, 10061, 11001, 18456, 40613, 40197, 40501];

    /// <summary>The <see cref="AppException"/> to return for a database failure, or null when <paramref name="ex"/> isn't one.</summary>
    public static AppException? Map(Exception ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return AppException.Conflict("CONFLICT", "Changed by someone else",
                "A teammate changed this record a moment ago. Reload to see their changes, then try again.");

        var s = Find(ex);
        if (s is null) return null;
        var n = s.Number;

        // Business rules THROWn by stored procedures as 'CODE: detail' (see database/03_sqlserver2012.sql).
        if (n is >= 50001 and <= 50099)
        {
            var i = s.Message.IndexOf(':');
            var code = i > 0 ? s.Message[..i].Trim() : "BUSINESS_RULE";
            var detail = i > 0 ? s.Message[(i + 1)..].Trim() : s.Message;
            var status = n switch { <= 50003 => 403, 50004 => 404, 50009 => 409, 50010 => 422, _ => 400 };
            var title = code switch
            {
                "PERMISSION_DENIED" => "Permission denied",
                "NOT_FOUND" => "Not found",
                "CONFLICT" => "Changed by someone else",
                "ONBOARDING_INCOMPLETE" => "Onboarding not complete",
                "INVALID_STAGE" => "Invalid stage",
                _ => "Action not allowed",
            };
            return new AppException(status, code, title, detail);
        }

        if (ConnectionErrors.Contains(n))
            return new AppException(503, "DB_UNAVAILABLE", "Database unavailable", "The database can't be reached right now. Please try again in a moment.");

        return n switch
        {
            2627 or 2601 => AppException.Conflict("DUPLICATE", "Already exists", "A record with the same unique value already exists."),
            547 => AppException.Bad("CONSTRAINT", "Invalid data",
                "The data breaks a database rule (for example an allowed value, a required link, or submit rate not above pay rate)."),
            33504 => AppException.Forbidden("That change would move the record outside your team, which your role isn't allowed to do."),
            1205 => AppException.Conflict("DEADLOCK", "Busy, please retry", "The database was busy with another change. Please try again."),
            -2 => new AppException(504, "DB_TIMEOUT", "The request timed out", "The database took too long to respond. Please try again."),
            _ => new AppException(500, "DB_ERROR", "Database error", $"A database error occurred (#{n}). Please try again or contact your admin."),
        };
    }
}
