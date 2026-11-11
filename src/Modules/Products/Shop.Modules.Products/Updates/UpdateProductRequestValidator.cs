using FluentValidation;

namespace Shop.Modules.Products.Updates;

internal sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Description).NotEmpty().MaximumLength(2000);
        RuleFor(request => request.Price)
            .Must(price => price is >= 0m and <= 9999999999999999.99m && decimal.Round(price.Value, 2) == price.Value)
            .WithMessage("Price is required and must be nonnegative with at most 16 integer digits and 2 decimal places.");
        RuleFor(request => request.CategoryId).Must(category => category is not null && category != Guid.Empty)
            .WithMessage("A category is required.");
    }
}
