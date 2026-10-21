using System.Net.Http.Headers;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;

namespace FirstBlick.Api;

/// <summary>Read-only previews: neither successful parsing nor a digest authorizes business posting.</summary>
public static class TradePreviewEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        TradeSourceEndpoints.Map(api);
        api.MapPost("/integrations/v1/e-invoice/preview", async (HttpContext http, TenantScope scope,
            CommandExecutor commands) =>
        {
            await commands.Authorize("supplier-invoices", false, http.RequestAborted);
            Guard.Require(scope.Role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting,
                "forbidden", "Invoice previews require an authorized finance role.");
            return Results.Ok(ElectronicInvoiceXml.Preview(await ReadXml(http)));
        }).WithTags("integrations");
    }

    public static async Task<byte[]> ReadXml(HttpContext http)
    {
        Guard.Require(MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            && mediaType.MediaType is "application/xml" or "text/xml"
            && (string.IsNullOrWhiteSpace(mediaType.CharSet)
                || mediaType.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)),
            "xml_content_type", "Supply UTF-8 application/xml or text/xml.");
        Guard.Require(http.Request.ContentLength is null or <= TradeXml.MaximumBytes,
            "xml_budget", "XML input is limited to 1 MiB.");
        using var result = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted);
            if (count == 0) break;
            Guard.Require(result.Length + count <= TradeXml.MaximumBytes,
                "xml_budget", "XML input is limited to 1 MiB.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
