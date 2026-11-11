using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Shop.Modules.Products.IntegrationTests;

// Gates sit outside SQL execution, so competing transactions never depend on a timing guess.
public sealed class UpdateSaveProbe : SaveChangesInterceptor
{
    private QueryGate? _before;
    private QueryGate? _after;

    public QueryGate PauseBeforeSave() => _before = new();
    public QueryGate PauseAfterCommit() => _after = new();
    public void Reset() { _before = null; _after = null; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var gate = Interlocked.Exchange(ref _before, null);
        if (gate is not null)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var gate = Interlocked.Exchange(ref _after, null);
        if (gate is not null)
        {
            gate.Entered.TrySetResult();
            // The transaction is already committed; let the test disconnect its client before the handler resumes.
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        return result;
    }
}
