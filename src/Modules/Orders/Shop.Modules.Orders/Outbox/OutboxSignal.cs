using System.Threading.Channels;

namespace Shop.Modules.Orders.Outbox;

/// <summary>
/// Wakes this instance's relay as soon as a save writes outbox rows, so an event usually leaves within milliseconds
/// instead of waiting for the next poll. Many saves while the relay is busy add up to one wake-up: a pass takes every
/// waiting row anyway.
/// </summary>
internal sealed class OutboxSignal
{
    private readonly Channel<bool> _wakeUps = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _wakeUps.Writer.TryWrite(true);

    /// <summary>Returns when <see cref="Notify"/> is called, or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        poll.CancelAfter(timeout);
        try
        {
            await _wakeUps.Reader.ReadAsync(poll.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No save in time: poll anyway, for rows another instance wrote or a wake-up this one missed
        }
    }
}
