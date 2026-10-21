using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FirstBlick.Api;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Integrations;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;
using static FirstBlick.Api.Tests.ExchangeApiHarness;

namespace FirstBlick.Api.Tests;

[Collection("Official XML engine")]
public sealed class OfficialInvoiceWorkflowTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string PreviewPath = "/api/v1/integrations/v2/e-invoice/import-preview";
    private const string ImportPath = "/api/v1/integrations/v2/e-invoice/import";
    private static (Invoice Source, byte[] Xml) Source()
    {
        var source = OfficialInvoiceFixtures.Invoice();
        return (source, ElectronicInvoiceOutput.Render(source, OfficialInvoiceFixtures.Mapping(source)).Xml);
    }
    private static ElectronicImportRequest Request(Supplier supplier, Invoice source, byte[] xml) => new(
        Convert.ToBase64String(xml), Convert.ToHexStringLower(SHA256.HashData(xml)), supplier.Id, supplier.Revision,
        source.SellerName, source.BuyerName, "Synthetic explicit legal-recipient and supplier mapping review");
    private async Task<Supplier> Supplier(ApiTestUser user, string name = "Synthetic Seller GmbH")
        => await fixture.Save(new Supplier { TenantId = user.TenantId, Name = name, Address = "Teststrasse 1, 10115 Berlin" }, user.UserId);
    private async Task<int> Documents(Guid tenant) => await fixture.InScope(async sp => await sp.GetRequiredService<FirstBlickDbContext>().Documents.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant));
    private async Task<int> Payables(Guid tenant) => await fixture.InScope(async sp => await sp.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == "supplier-invoices"));

    [PostgresApiFact]
    public async Task Validated_original_is_archived_booked_as_draft_approved_and_reconciled_without_initiating_payment()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source(); var request = Request(supplier, source.Source, source.Xml);
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        Assert.True(preview.Validation.Accepted); Assert.True(preview.CanImport); Assert.False(preview.AutomaticallyApproved);
        Assert.Equal(99.99m, preview.Facts!.Net); Assert.Equal(19m, preview.Facts.Tax);
        Assert.Equal(0, await Payables(tenant.Id)); Assert.Equal(0, await Documents(tenant.Id));
        request = request with { PreviewDigest = preview.Digest };
        var key = Guid.NewGuid().ToString("N");
        var imported = await Ok<JsonElement>(Send(client, HttpMethod.Post, ImportPath, request, key: key));
        var invoice = imported.GetProperty("invoice").Deserialize<SupplierInvoice>(AppJson.Options)!;
        Assert.Equal(SupplierInvoiceStatus.Draft, invoice.Status); Assert.NotNull(invoice.ElectronicSource);
        Assert.False(imported.GetProperty("automaticallyApproved").GetBoolean()); Assert.False(imported.GetProperty("paymentInitiated").GetBoolean());
        invoice.Validate(); Assert.Equal(3, await Documents(tenant.Id)); Assert.Equal(3, host.Store.PutCount);
        var documents = imported.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(source.Xml, await Download(client, documents.Single(x => x.GetProperty("id").GetGuid() == invoice.ElectronicSource!.OriginalDocumentId)));
        var report = await Download(client, documents.Single(x => x.GetProperty("id").GetGuid() == invoice.ElectronicSource!.ValidationDocumentId));
        Assert.Equal(invoice.ElectronicSource!.ValidationReportSha256, Convert.ToHexStringLower(SHA256.HashData(report)));
        var receipt = await Download(client, documents.Single(x => x.GetProperty("id").GetGuid() == invoice.ElectronicSource!.ReceiptDocumentId));
        using var retainedReceipt = JsonDocument.Parse(receipt);
        Assert.Equal(preview.Digest, retainedReceipt.RootElement.GetProperty("reviewedPreviewDigest").GetString());
        using var replay = await Send(client, HttpMethod.Post, ImportPath, request, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, await Payables(tenant.Id)); Assert.Equal(3, await Documents(tenant.Id)); Assert.Equal(3, host.Store.PutCount);

        invoice = await Ok<SupplierInvoice>(Send(client, HttpMethod.Post, $"/api/v1/supplier-invoices/{invoice.Id}/approve", new { }, invoice.Revision));
        Assert.Equal(SupplierInvoiceStatus.Approved, invoice.Status); invoice.Validate();
        var balance = await Ok<PayableBalance>(client.GetAsync($"/api/v1/supplier-invoices/{invoice.Id}/balance"));
        Assert.Equal(118.99m, balance.Outstanding);
        var bankResponse = await Ok<JsonElement>(Send(client, HttpMethod.Post, "/api/v1/bank-transactions/import", new BankImportRequest("synthetic-read-only-account",
            [new("synthetic-settled-transfer", new(2026, 10, 21), -118.99m, supplier.Name, invoice.Number)])));
        var bank = bankResponse.GetProperty("items")[0].Deserialize<BankTransaction>(AppJson.Options)!;
        var allocation = await Ok<SupplierPaymentAllocation>(Send(client, HttpMethod.Post, "/api/v1/supplier-payments/allocate",
            new SupplierPaymentRequest(invoice.Id, bank.Id, 118.99m, Reason: "Synthetic already booked bank transfer"), bank.Revision));
        Assert.Equal(0, (await Ok<PayableBalance>(client.GetAsync($"/api/v1/supplier-invoices/{invoice.Id}/balance"))).Outstanding);
        await Ok<SupplierPaymentAllocation>(Send(client, HttpMethod.Post, $"/api/v1/supplier-payments/{allocation.Id}/reverse",
            new ReversePaymentRequest("Synthetic reconciliation reversal, not a payment reversal"), allocation.Revision));
        Assert.Equal(118.99m, (await Ok<PayableBalance>(client.GetAsync($"/api/v1/supplier-invoices/{invoice.Id}/balance"))).Outstanding);
        Assert.Equal(3, host.Store.PutCount);
    }

    [PostgresApiFact]
    public async Task Imported_financial_values_and_origin_cannot_be_edited_or_removed_even_while_draft()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source(); var request = Request(supplier, source.Source, source.Xml);
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        var response = await Ok<JsonElement>(Send(client, HttpMethod.Post, ImportPath, request with { PreviewDigest = preview.Digest }));
        var invoice = response.GetProperty("invoice").Deserialize<SupplierInvoice>(AppJson.Options)!;
        var originalEvidence = invoice.ElectronicSource;
        invoice.ElectronicSource = null;
        Assert.Equal("einvoice_evidence_changed", await Error(Send(client, HttpMethod.Put, $"/api/v1/supplier-invoices/{invoice.Id}", invoice, invoice.Revision)));
        invoice.ElectronicSource = originalEvidence; invoice.Net += 1; invoice.Gross += 1;
        Assert.Equal("einvoice_evidence_changed", await Error(Send(client, HttpMethod.Put, $"/api/v1/supplier-invoices/{invoice.Id}", invoice, invoice.Revision)));
        var stored = await fixture.Read<SupplierInvoice>(tenant.Id, invoice.Id); stored.Validate();
        Assert.Equal(99.99m, stored.Net); Assert.Equal(originalEvidence, stored.ElectronicSource);
        var duplicate = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        Assert.False(duplicate.CanImport); Assert.Contains("identical_original_xml_already_retained_for_a_payable", duplicate.BlockingReasons);
        Assert.Contains("supplier_invoice_number_already_exists", duplicate.BlockingReasons);
    }

    [PostgresApiFact]
    public async Task A_changed_mapping_or_source_file_cannot_reuse_the_reviewed_booking_digest()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source(); var request = Request(supplier, source.Source, source.Xml);
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        Assert.Equal("einvoice_preview_changed", await Error(Send(client, HttpMethod.Post, ImportPath,
            request with { PreviewDigest = preview.Digest, ReviewEvidence = "Different recipient mapping review" })));
        Assert.Equal("source_hash_mismatch", await Error(Send(client, HttpMethod.Post, PreviewPath, request with { ExpectedSourceSha256 = new('0', 64) })));
        Assert.Equal("party_review_mismatch", await Error(Send(client, HttpMethod.Post, PreviewPath, request with { ReviewedSourceBuyerName = "Different legal recipient" })));
        Assert.Equal("supplier_changed", await Error(Send(client, HttpMethod.Post, PreviewPath, request with { ExpectedSupplierRevision = supplier.Revision + 1 })));
        Assert.Equal(0, await Payables(tenant.Id)); Assert.Equal(0, await Documents(tenant.Id)); Assert.Equal(0, host.Store.PutCount);
    }

    [PostgresApiFact]
    public async Task Storage_readback_failure_rolls_back_the_payable_and_downloadable_metadata()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source(); var request = Request(supplier, source.Source, source.Xml);
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        host.Store.CorruptReads = true;
        Assert.Equal("document_integrity", await Error(Send(client, HttpMethod.Post, ImportPath, request with { PreviewDigest = preview.Digest })));
        Assert.Equal(0, await Payables(tenant.Id)); Assert.Equal(0, await Documents(tenant.Id));
        Assert.True(host.Store.PutCount > 0); // An unreferenced object version is not falsely described as an atomic S3 rollback.
        host.Store.CorruptReads = false;
        var success = await Ok<JsonElement>(Send(client, HttpMethod.Post, ImportPath, request with { PreviewDigest = preview.Digest }));
        Assert.Equal("Draft", success.GetProperty("invoice").GetProperty("status").GetString());
        Assert.Equal(1, await Payables(tenant.Id)); Assert.Equal(3, await Documents(tenant.Id));
    }

    [PostgresApiFact]
    public async Task Another_tenant_and_a_worker_cannot_book_or_download_the_validated_source()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source(); var request = Request(supplier, source.Source, source.Xml);
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, request));
        var booked = await Ok<JsonElement>(Send(client, HttpMethod.Post, ImportPath, request with { PreviewDigest = preview.Digest }));
        var another = await fixture.CreateTenant(); using var foreign = await host.Login(await fixture.CreateUser(another.Id));
        using var absent = await foreign.GetAsync($"/api/v1/documents/{booked.GetProperty("documents")[0].GetProperty("id").GetGuid()}/download");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var wrongSupplier = await Send(foreign, HttpMethod.Post, PreviewPath, request);
        Assert.Equal(HttpStatusCode.NotFound, wrongSupplier.StatusCode);
        using var worker = await host.Login(await fixture.CreateUser(tenant.Id, BusinessRole.Worker));
        using var denied = await Send(worker, HttpMethod.Post, ImportPath, request with { PreviewDigest = preview.Digest });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, await Payables(another.Id));
    }

    [PostgresApiFact]
    public async Task A_schema_parseable_but_invalid_invoice_cannot_create_any_payable_or_archive()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var supplier = await Supplier(user); var source = Source();
        var document = TradeXml.Load(source.Xml).Document;
        System.Xml.Linq.XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        document.Descendants(cbc + "PayableAmount").Single().Value = "999.00";
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToString());
        var preview = await Ok<ElectronicImportPreview>(Send(client, HttpMethod.Post, PreviewPath, Request(supplier, source.Source, bytes)));
        Assert.False(preview.Validation.Accepted); Assert.False(preview.CanImport); Assert.Null(preview.Digest);
        Assert.Equal(0, await Payables(tenant.Id)); Assert.Equal(0, await Documents(tenant.Id)); Assert.Equal(0, host.Store.PutCount);
    }

    [PostgresApiFact]
    public async Task Issued_receivable_exports_only_validated_original_bytes_and_does_not_rewrite_money()
    {
        var tenant = await fixture.CreateTenant("Synthetic Seller GmbH"); var user = await fixture.CreateUser(tenant.Id);
        await using var host = new ExchangeApiHarness(fixture); using var client = await host.Login(user);
        var customer = await Ok<Customer>(Send(client, HttpMethod.Post, "/api/v1/customers", new Customer { Name = "Synthetic Buyer GmbH", Address = "Teststrasse 2, 10115 Berlin" }));
        var building = await Ok<Building>(Send(client, HttpMethod.Post, "/api/v1/buildings", new Building { CustomerId = customer.Id, Name = "Synthetic roof", Address = customer.Address }));
        var project = await Ok<Project>(Send(client, HttpMethod.Post, "/api/v1/projects", new Project { CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic structured output" }));
        var quote = await Ok<Quote>(Send(client, HttpMethod.Post, "/api/v1/quotes", new Quote { CustomerId = customer.Id, ProjectId = project.Id, Title = "Synthetic contract",
            Lines = [new() { Description = "Synthetic installation", Unit = "Stk", Quantity = 3, UnitMaterialCost = 33.33m, VatPercent = 19 }] }));
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision));
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new AcceptQuoteRequest(quote.IssuedDigest), quote.Revision));
        var order = await Ok<Order>(Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision));
        var invoice = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/from-order/{order.Id}", new InvoiceFromOrderRequest(InvoiceKind.Final), order.Revision));
        invoice.SellerAddress = "Teststrasse 1, 10115 Berlin"; invoice.SellerTaxId = "DE123456789";
        invoice = await Ok<Invoice>(Send(client, HttpMethod.Put, $"/api/v1/invoices/{invoice.Id}", invoice, invoice.Revision));
        invoice = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/issue", new { }, invoice.Revision));
        var issuedDigest = invoice.IssuedDigest;
        var request = new ElectronicOutputRequest(OfficialInvoiceFixtures.Mapping(invoice));
        var preview = await Ok<ElectronicOutputPreview>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/e-invoice/preview", request, invoice.Revision));
        Assert.True(preview.CanExport, string.Join("; ", preview.Validation.Issues.Select(x => x.Message)));
        var key = Guid.NewGuid().ToString("N"); request = request with { PreviewDigest = preview.Digest };
        var exported = await Ok<JsonElement>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/e-invoice/export", request, invoice.Revision, key));
        Assert.False(exported.GetProperty("externallySent").GetBoolean()); Assert.False(exported.GetProperty("invoiceMoneyChanged").GetBoolean());
        var xml = await Download(client, exported.GetProperty("documents")[0]);
        Assert.Equal(preview.XmlSha256, Convert.ToHexStringLower(SHA256.HashData(xml)));
        var independentlyValidated = await new KositInvoiceValidator(OfficialXmlValidationTests.Bundle()).Validate(xml);
        Assert.True(independentlyValidated.Accepted);
        var stored = await fixture.Read<Invoice>(tenant.Id, invoice.Id);
        Assert.Equal(issuedDigest, stored.IssuedDigest); Assert.Equal(118.99m, stored.Snapshot!.Gross);
        using var replay = await Send(client, HttpMethod.Post, $"/api/v1/invoices/{invoice.Id}/e-invoice/export", request, invoice.Revision, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(3, host.Store.PutCount); Assert.Equal(3, await Documents(tenant.Id));
    }
}
