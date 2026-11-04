using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core;
using Inventory.V1;

namespace Shop.Modules.Products.UnitTests;

/// <summary>
/// Plays the Go warehouse in-process. It records every call with the time its deadline allowed, and each
/// test scripts the answers.
/// </summary>
internal sealed class FakeWarehouse : InventoryService.InventoryServiceBase
{
    public ConcurrentQueue<WarehouseCall> Calls { get; } = new();

    public Func<GetStockRequest, ServerCallContext, Task<GetStockResponse>> OnGetStock { get; set; } =
        (_, _) => Task.FromResult(new GetStockResponse());

    public Func<ReserveRequest, ServerCallContext, Task<ReserveResponse>> OnReserve { get; set; } =
        (_, _) => Task.FromResult(new ReserveResponse { Reserved = true });

    public Func<ReleaseRequest, ServerCallContext, Task<ReleaseResponse>> OnRelease { get; set; } =
        (_, _) => Task.FromResult(new ReleaseResponse());

    public override Task<GetStockResponse> GetStock(GetStockRequest request, ServerCallContext context)
    {
        Record(nameof(GetStock), request, context);
        return OnGetStock(request, context);
    }

    public override Task<ReserveResponse> Reserve(ReserveRequest request, ServerCallContext context)
    {
        Record(nameof(Reserve), request, context);
        return OnReserve(request, context);
    }

    public override Task<ReleaseResponse> Release(ReleaseRequest request, ServerCallContext context)
    {
        Record(nameof(Release), request, context);
        return OnRelease(request, context);
    }

    public IReadOnlyList<WarehouseCall> CallsTo(string method) => [.. Calls.Where(call => call.Method == method)];

    private void Record(string method, IMessage request, ServerCallContext context) =>
        Calls.Enqueue(new WarehouseCall(method, request, context.Deadline - DateTime.UtcNow));
}

/// <summary>One call as the warehouse saw it, with how long the caller's deadline still allowed.</summary>
internal sealed record WarehouseCall(string Method, IMessage Request, TimeSpan TimeLeft);
