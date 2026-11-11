using FluentValidation;

namespace Shop.Modules.Products.Catalog;

internal sealed class ListProductsRequestValidator : AbstractValidator<ListProductsRequest>
{
    public ListProductsRequestValidator()
    {
        RuleFor(request => request.Category)
            .Must(category => category is null || category != Guid.Empty)
            .WithMessage("Category must be a non-empty UUID.");
        RuleFor(request => request.Page).GreaterThan(0).When(request => request.Page is not null);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 50);

        // EF's Skip accepts an int; reject offsets that would overflow before querying the database.
        RuleFor(request => request.Page)
            .Must((request, page) => ((long)(page ?? 1) - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");

        RuleFor(request => request.Cursor)
            .Must(cursor => ProductCursor.TryDecode(cursor, out _))
            .When(request => request.Cursor is not null)
            .WithMessage("The cursor must be a nextCursor from an earlier page.");
        RuleFor(request => request.Cursor)
            .Null()
            .When(request => request.Page is not null)
            .WithMessage("Ask for a page or a cursor, not both.");
    }
}
