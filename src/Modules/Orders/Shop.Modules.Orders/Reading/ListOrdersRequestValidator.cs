using FluentValidation;

namespace Shop.Modules.Orders.Reading;

internal sealed class ListOrdersRequestValidator : AbstractValidator<ListOrdersRequest>
{
    public ListOrdersRequestValidator()
    {
        // Query binding accepts numbers too, so "?status=42" would otherwise reach the query
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Page).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 50);

        // EF's Skip accepts an int; reject offsets that would overflow before querying the database.
        RuleFor(request => request.Page)
            .Must((request, page) => ((long)page - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
    }
}
