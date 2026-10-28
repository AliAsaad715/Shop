using System.Reflection;
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
    /// <c>AddValidatorsFromAssemblyContaining</c>. Only the validated endpoints document the 422.
    /// </summary>
    public static TBuilder WithRequestValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder.Metadata.OfType<MethodInfo>().FirstOrDefault() is { } handler
                && ValidatedArguments(handler, endpointBuilder.ApplicationServices).Count > 0)
            {
                endpointBuilder.Metadata.Add(new ProducesResponseTypeMetadata(
                    StatusCodes.Status422UnprocessableEntity, typeof(HttpValidationProblemDetails), ["application/problem+json"]));
            }
        });

        builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var validated = ValidatedArguments(factoryContext.MethodInfo, factoryContext.ApplicationServices);

            return async invocationContext =>
            {
                // Binding failures already set 400; never validate default arguments or invoke the handler.
                if (invocationContext.HttpContext.Response.StatusCode == StatusCodes.Status400BadRequest)
                {
                    return Problems.Create(StatusCodes.Status400BadRequest, ErrorCodes.Common.BadRequest, "Bad Request");
                }

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

    private static List<(int Index, Type ValidatorType)> ValidatedArguments(MethodInfo handler, IServiceProvider services)
    {
        var registrations = services.GetRequiredService<IServiceProviderIsService>();
        var parameters = handler.GetParameters();
        var validated = new List<(int Index, Type ValidatorType)>();
        for (var index = 0; index < parameters.Length; index++)
        {
            if (ValidatorTypeFor(parameters[index].ParameterType) is { } validatorType && registrations.IsService(validatorType))
            {
                validated.Add((index, validatorType));
            }
        }

        return validated;
    }

    private static Type? ValidatorTypeFor(Type parameterType) =>
        parameterType.IsByRef || parameterType.IsPointer || parameterType.ContainsGenericParameters
            ? null
            : typeof(IValidator<>).MakeGenericType(parameterType);

    // "Lines[0].Quantity" becomes "lines[0].quantity", matching the JSON the client sent
    private static string ToJsonPath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
