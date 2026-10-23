using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text.Json.Serialization.Metadata;

namespace FirstBlick.Api;

/// <summary>Describe the existing decimal converter without changing runtime values.</summary>
public static class OpenApiWireContracts
{
    public static IServiceCollection AddFirstBlickOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1");
        return services.AddOpenApi("wire-v1", options =>
        {
            // Only models that contain decimals need separate input and output
            // instances. Other models retain references for recursive schemas.
            options.CreateSchemaReferenceId = info => ContainsDecimal(info.Type, [], 0)
                ? null : OpenApiOptions.CreateDefaultSchemaReferenceId(info);
            Configure(options);
        });
    }

    public static void Configure(OpenApiOptions options) => options.AddSchemaTransformer((schema, context, _) =>
    {
        var input = context.ParameterDescription is not null;
        var type = context.JsonTypeInfo.Type;
        if ((Nullable.GetUnderlyingType(type) ?? type) == typeof(decimal))
            Decimal(schema, type, input);
        else if (context.JsonTypeInfo.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
        {
            var element = type.GetInterfaces().Prepend(type)
                .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GenericTypeArguments[0];
            if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Dictionary && element?.IsGenericType == true
                && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) element = element.GenericTypeArguments[1];
            if (element is not null && (Nullable.GetUnderlyingType(element) ?? element) == typeof(decimal))
            {
                var item = new OpenApiSchema();
                Decimal(item, element, input);
                if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Dictionary) schema.AdditionalProperties = item;
                else schema.Items = item;
            }
        }
        return Task.CompletedTask;
    });

    private static void Decimal(OpenApiSchema schema, Type type, bool input)
    {
        var nullable = Nullable.GetUnderlyingType(type) is not null || (schema.Type & JsonSchemaType.Null) == JsonSchemaType.Null;
        schema.Type = JsonSchemaType.String | (input ? JsonSchemaType.Number : 0)
            | (nullable ? JsonSchemaType.Null : 0);
        schema.Format = null;
        schema.Pattern = input ? @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)$" : @"^-?[0-9]+(?:\.[0-9]+)?$";
        schema.AnyOf = null;
        schema.OneOf = null;
        schema.AllOf = null;
        schema.Description = input
            ? "Invariant decimal string or JSON number; the server validates .NET decimal range and precision. Strings have no grouping or exponent notation."
            : "Exact .NET decimal serialized as an invariant JSON string, never a binary floating-point JSON number.";
    }

    private static bool ContainsDecimal(Type type, HashSet<Type> seen, int depth)
    {
        if ((Nullable.GetUnderlyingType(type) ?? type) == typeof(decimal)) return true;
        if (depth >= 16 || !seen.Add(type)) return false;
        try
        {
            if (type.IsArray) return ContainsDecimal(type.GetElementType()!, seen, depth + 1);
            if (type.IsGenericType && type.GetGenericArguments().Any(arg => ContainsDecimal(arg, seen, depth + 1))) return true;
            if (type.Namespace?.StartsWith("FirstBlick.", StringComparison.Ordinal) != true) return false;
            return type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Any(property => ContainsDecimal(property.PropertyType, seen, depth + 1));
        }
        finally { seen.Remove(type); }
    }
}
