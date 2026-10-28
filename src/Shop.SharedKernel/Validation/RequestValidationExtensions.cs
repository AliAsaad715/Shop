using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shop.SharedKernel.Errors;

namespace Shop.SharedKernel.Validation;

public static class RequestValidationExtensions
{
    /// <summary>
    /// Validates every endpoint argument whose type has an <see cref="IValidator{T}"/> registered and
    /// answers 422 with the failures before the handler runs. Modules register their validators with
    /// <c>AddValidatorsFromAssemblyContaining</c>.
    /// </summary>
    public static TBuilder WithRequestValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var registrations = factoryContext.ApplicationServices.GetRequiredService<IServiceProviderIsService>();
            var parameters = factoryContext.MethodInfo.GetParameters();
            var validated = new List<(int Index, Type ValidatorType)>();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (ValidatorTypeFor(parameters[index].ParameterType) is { } validatorType && registrations.IsService(validatorType))
                {
                    validated.Add((index, validatorType));
                }
            }

            // Endpoints with nothing to validate skip the filter entirely
            if (validated.Count == 0)
            {
                return next;
            }

            return async invocationContext =>
            {
                var errors = new Dictionary<string, string[]>();
                foreach (var (index, validatorType) in validated)
                {
                    if (invocationContext.Arguments[index] is not { } argument)
                    {
                        continue;
                    }

                    var validator = (IValidator)invocationContext.HttpContext.RequestServices.GetRequiredService(validatorType);
                    var result = await validator.ValidateAsync(new ValidationContext<object>(argument), invocationContext.HttpContext.RequestAborted);
                    foreach (var (property, messages) in result.ToDictionary())
                    {
                        errors[ToJsonPath(property)] = messages;
                    }
                }

                return errors.Count == 0 ? await next(invocationContext) : Problems.Validation(errors);
            };
        });

        return builder;
    }

    private static Type? ValidatorTypeFor(Type parameterType) =>
        parameterType.IsByRef || parameterType.IsPointer || parameterType.ContainsGenericParameters
            ? null
            : typeof(IValidator<>).MakeGenericType(parameterType);

    // "Lines[0].Quantity" becomes "lines[0].quantity", matching the JSON the client sent
    private static string ToJsonPath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
