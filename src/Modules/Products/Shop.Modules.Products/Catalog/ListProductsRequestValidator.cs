using FluentValidation;

namespace Shop.Modules.Products.Catalog;

internal sealed class ListProductsRequestValidator : AbstractValidator<ListProductsRequest>
{
    public ListProductsRequestValidator()
    {
        RuleFor(request => request.Category)
            .Must(category => category is null || category != Guid.Empty)
            .WithMessage("Category must be a non-empty UUID.");
        RuleFor(request => request.Page).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 50);

        // EF's Skip accepts an int; reject offsets that would overflow before querying the database.
        RuleFor(request => request.Page)
            .Must((request, page) => ((long)page - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
    }
}
