using Shop.Modules.Orders.Domain;
using Shop.Modules.Orders.Placement;
using Shop.Modules.Orders.Reading;
using Shop.Modules.Orders.StatusChanges;
using Xunit;

namespace Shop.Modules.Orders.UnitTests;

public sealed class OrderRequestValidatorTests
{
    private static readonly Guid _book = Guid.CreateVersion7();
    private static readonly Guid _lamp = Guid.CreateVersion7();

    [Fact]
    public void ValidOrderPasses() =>
        Assert.True(new CreateOrderRequestValidator().Validate(new CreateOrderRequest([new(_book, 2), new(_lamp, 1000)])).IsValid);

    [Fact]
    public void MissingOrEmptyLinesFail()
    {
        var validator = new CreateOrderRequestValidator();

        Assert.False(validator.Validate(new CreateOrderRequest(null)).IsValid);
        Assert.False(validator.Validate(new CreateOrderRequest([])).IsValid);
    }

    [Fact]
    public void TooManyLinesFail()
    {
        var lines = Enumerable.Range(0, CreateOrderRequestValidator.MaxLines + 1)
            .Select(_ => new CreateOrderLineRequest(Guid.CreateVersion7(), 1)).ToList();

        Assert.False(new CreateOrderRequestValidator().Validate(new CreateOrderRequest(lines)).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void BadQuantityFailsOnThatLine(int? quantity)
    {
        var result = new CreateOrderRequestValidator().Validate(new CreateOrderRequest([new(_book, 1), new(_lamp, quantity)]));

        Assert.Equal("Lines[1].Quantity", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void MissingOrEmptyProductIdFails()
    {
        var result = new CreateOrderRequestValidator().Validate(new CreateOrderRequest([new(null, 1), new(Guid.Empty, 1)]));

        Assert.Equal(new[] { "Lines[0].ProductId", "Lines[1].ProductId" }, result.Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void NullLineFails() =>
        Assert.False(new CreateOrderRequestValidator().Validate(new CreateOrderRequest([null!])).IsValid);

    [Fact]
    public void DuplicateProductFails() =>
        Assert.False(new CreateOrderRequestValidator().Validate(new CreateOrderRequest([new(_book, 1), new(_book, 2)])).IsValid);

    [Fact]
    public void StatusIsRequiredAndMustBeKnown()
    {
        var validator = new UpdateOrderStatusRequestValidator();

        Assert.True(validator.Validate(new UpdateOrderStatusRequest(OrderStatus.Shipped)).IsValid);
        Assert.False(validator.Validate(new UpdateOrderStatusRequest(null)).IsValid);
        Assert.False(validator.Validate(new UpdateOrderStatusRequest((OrderStatus)42)).IsValid);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    [InlineData(int.MaxValue, 50)]
    public void ListPagingOutsideTheLimitsFails(int page, int pageSize) =>
        Assert.False(new ListOrdersRequestValidator().Validate(new ListOrdersRequest(null, page, pageSize)).IsValid);

    [Fact]
    public void UnknownStatusFilterFails() =>
        Assert.False(new ListOrdersRequestValidator().Validate(new ListOrdersRequest((OrderStatus)42)).IsValid);
}
