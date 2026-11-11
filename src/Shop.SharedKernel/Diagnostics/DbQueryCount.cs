namespace Shop.SharedKernel.Diagnostics;

/// <summary>
/// How many SQL commands the current request has sent, kept as a request feature while the development-only
/// X-Db-Queries header is on (<see cref="DbQueryCountHeaderExtensions.UseDbQueryCountHeader"/>).
/// </summary>
public sealed class DbQueryCount
{
    private static readonly AsyncLocal<DbQueryCount?> Active = new();
    private int _value;

    public int Value => Volatile.Read(ref _value);

    internal void Increment() => Interlocked.Increment(ref _value);

    internal static DbQueryCount? Current => Active.Value;

    // A shared cache fill can run outside the initiating request's execution context. Carry only
    // its detached counter into that work, never HttpContext or a request-owned DbContext.
    public IDisposable Activate()
    {
        var previous = Active.Value;
        Active.Value = this;
        return new Activation(previous);
    }

    private sealed class Activation(DbQueryCount? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
