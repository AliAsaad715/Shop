namespace Shop.SharedKernel.Diagnostics;

/// <summary>
/// How many SQL commands the current request has sent, kept as a request feature while the development-only
/// X-Db-Queries header is on (<see cref="DbQueryCountHeaderExtensions.UseDbQueryCountHeader"/>).
/// </summary>
public sealed class DbQueryCount
{
    // The count of the request this code runs for. It flows with the request's awaits; a cache load, which HybridCache
    // runs apart from the request, takes it along (CacheReads.GetOrLoadAsync)
    internal static readonly AsyncLocal<DbQueryCount?> Current = new();

    private int _value;

    public int Value => Volatile.Read(ref _value);

    internal void Increment() => Interlocked.Increment(ref _value);
}
