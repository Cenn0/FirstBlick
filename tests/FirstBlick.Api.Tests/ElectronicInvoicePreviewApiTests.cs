using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class ElectronicInvoicePreviewApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string Xml = """
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
            xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
            xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
        <cbc:ID>TEST-2026-1</cbc:ID><cbc:IssueDate>2026-10-20</cbc:IssueDate>
        <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
        <cac:AccountingSupplierParty><cac:Party><cac:PartyName><cbc:Name>External Supplier</cbc:Name></cac:PartyName></cac:Party></cac:AccountingSupplierParty>
        <cac:AccountingCustomerParty><cac:Party><cac:PartyName><cbc:Name>Roof Workshop</cbc:Name></cac:PartyName></cac:Party></cac:AccountingCustomerParty>
        <cac:LegalMonetaryTotal><cbc:TaxExclusiveAmount>50.00</cbc:TaxExclusiveAmount>
          <cbc:TaxInclusiveAmount>59.50</cbc:TaxInclusiveAmount><cbc:PayableAmount>59.50</cbc:PayableAmount></cac:LegalMonetaryTotal>
        <cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="C62">1</cbc:InvoicedQuantity>
          <cbc:LineExtensionAmount>50.00</cbc:LineExtensionAmount><cac:Item><cbc:Name>Repair</cbc:Name></cac:Item></cac:InvoiceLine>
        </Invoice>
        """;

    [PostgresApiFact]
    public async Task Explicit_preview_is_tenant_protected_and_never_creates_a_financial_document()
    {
        var tenant = await fixture.CreateTenant();
        var user = await fixture.CreateUser(tenant.Id, BusinessRole.Accounting);
        var client = await fixture.Login(user);
        using var result = await Post(client, Xml);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var preview = AppJson.Read<FirstBlick.Core.Integrations.ElectronicInvoicePreview>(
            await result.Content.ReadAsStringAsync());
        Assert.Equal("TEST-2026-1", preview.InvoiceNumber);
        Assert.False(preview.ExternalValidationPerformed);
        Assert.False(preview.MayPostAutomatically);
        await fixture.InScope(async sp =>
        {
            var db = sp.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(0, await db.Entities.IgnoreQueryFilters().CountAsync(x =>
                x.TenantId == tenant.Id && x.Kind == "supplier-invoices"));
        });
        var worker = await fixture.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var denied = await Post(worker, Xml);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [PostgresApiFact]
    public async Task XML_attachments_with_dtd_or_an_unsupported_media_type_fail_closed()
    {
        var tenant = await fixture.CreateTenant();
        var client = await fixture.Login(await fixture.CreateUser(tenant.Id));
        using var dtd = await Post(client, "<!DOCTYPE Invoice [<!ENTITY x SYSTEM 'file:///tmp/secret'>]><Invoice>&x;</Invoice>");
        Assert.Equal(HttpStatusCode.BadRequest, dtd.StatusCode);
        using var badMedia = await Post(client, Xml, "text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, badMedia.StatusCode);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string xml, string mediaType = "application/xml")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/v1/e-invoice/preview")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return client.SendAsync(request);
    }
}
