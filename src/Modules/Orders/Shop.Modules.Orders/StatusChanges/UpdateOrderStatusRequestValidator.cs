using FluentValidation;

namespace Shop.Modules.Orders.StatusChanges;

internal sealed class UpdateOrderStatusRequestValidator : AbstractValidator<UpdateOrderStatusRequest>
{
    public UpdateOrderStatusRequestValidator()
    {
        RuleFor(request => request.Status).NotNull().IsInEnum();
    }
}
