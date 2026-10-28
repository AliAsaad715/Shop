using FluentValidation;

namespace Shop.Modules.Orders.Placement;

internal sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 1000;

    public CreateOrderRequestValidator()
    {
        RuleFor(request => request.Lines)
            .Must(lines => lines is { Count: > 0 and <= MaxLines })
            .WithMessage($"An order needs between 1 and {MaxLines} lines.");

        // One line per product, so a quantity is never split across lines
        RuleFor(request => request.Lines)
            .Must(lines => lines!.Where(line => line?.ProductId is not null).GroupBy(line => line!.ProductId).All(group => group.Count() == 1))
            .When(request => request.Lines is not null)
            .WithMessage("Each product may appear on only one line.");

        // Child rules skip null elements, so a null line ("lines": [null]) is caught here first
        RuleForEach(request => request.Lines).NotNull().WithMessage("A line is required.").ChildRules(line =>
        {
            line.RuleFor(item => item.ProductId)
                .Must(productId => productId is not null && productId != Guid.Empty)
                .WithMessage("A product ID is required.");
            line.RuleFor(item => item.Quantity)
                .Must(quantity => quantity is >= 1 and <= MaxQuantity)
                .WithMessage($"Quantity must be between 1 and {MaxQuantity}.");
        });
    }
}
