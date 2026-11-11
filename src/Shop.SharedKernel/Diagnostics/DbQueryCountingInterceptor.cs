using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Shop.SharedKernel.Diagnostics;

/// <summary>
/// Counts every command EF Core sends, in any module's DbContext, toward the request's <see cref="DbQueryCount"/>.
/// Each command is one round trip to the database, so a list that runs a query per item shows up as a number.
/// Work outside a request, such as migrations or background workers, has no count and isn't counted.
/// </summary>
internal sealed class DbQueryCountingInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Count();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Count();
        return ValueTask.FromResult(result);
    }

    private static void Count() => DbQueryCount.Current.Value?.Increment();
}
