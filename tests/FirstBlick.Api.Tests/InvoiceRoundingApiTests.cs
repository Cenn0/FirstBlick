using System.Net;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FirstBlick.Api.Tests.PostgresApiFixture;

namespace FirstBlick.Api.Tests;

public sealed class InvoiceRoundingApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresApiFact]
    public async Task Real_http_issuance_persists_tax_carry_and_replay_does_not_duplicate_a_document_or_number()
    {
        var (user, client) = await Session(); var order = await Order(client);
        var progress = await Draft(client, order, InvoiceKind.Progress, 1m);
        progress = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/issue", new { }, progress.Revision));
        var final = await Draft(client, order, InvoiceKind.Final, 2m); var key = Guid.NewGuid().ToString("N"); var revision = final.Revision;
        final = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, revision, key));
        using var replay = await Send(client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, revision, key);
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(final.IssuedDigest, AppJson.Read<Invoice>(await replay.Content.ReadAsStringAsync()).IssuedDigest);
        Assert.Equal((.02m, .01m, .03m), (final.Snapshot!.Net, final.Snapshot.Tax, final.Snapshot.Gross));
        var stored = await fixture.Read<Invoice>(user.TenantId, final.Id); stored.Validate();
        Assert.Equal(final.Calculation!.Digest, stored.Calculation!.Digest);
        Assert.Equal(progress.IssuedDigest, stored.Calculation.PreviousInvoices.Single().Digest);
        Assert.Equal(order.Contract!.Digest, stored.Calculation.Sources.Single().DocumentDigest);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Kind == "invoices"
                && x.EntityId == final.Id && x.Action == "invoice.issue"));
            Assert.Equal(2, await db.Counters.IgnoreQueryFilters().Where(x => x.TenantId == user.TenantId && x.Kind == "RE").Select(x => x.Value).SingleAsync());
            var payload = await db.Entities.IgnoreQueryFilters().Where(x => x.TenantId == user.TenantId && x.Id == final.Id).Select(x => x.Data).SingleAsync();
            using var json = JsonDocument.Parse(payload);
            Assert.Equal("0.01", json.RootElement.GetProperty("calculation").GetProperty("lines")[0].GetProperty("afterTax").GetString());
        });
    }

    [PostgresApiFact]
    public async Task Competing_final_invoices_serialize_against_current_quantities_and_cannot_bill_the_same_remaining_cent_twice()
    {
        var (user, client) = await Session(); var order = await Order(client);
        var progress = await Draft(client, order, InvoiceKind.Progress, 1m);
        await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/issue", new { }, progress.Revision));
        var left = await Draft(client, order, InvoiceKind.Final, 2m); var right = await Draft(client, order, InvoiceKind.Final, 2m);
        var responses = await Task.WhenAll(Send(client, HttpMethod.Post, $"/api/v1/invoices/{left.Id}/issue", new { }, left.Revision),
            Send(client, HttpMethod.Post, $"/api/v1/invoices/{right.Id}/issue", new { }, right.Revision));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        await Error(Task.FromResult(responses.Single(x => !x.IsSuccessStatusCode)), HttpStatusCode.BadRequest, "order_overbilled");
        var successful = await Ok<Invoice>(Task.FromResult(responses.Single(x => x.IsSuccessStatusCode)));
        Assert.Equal(.03m, successful.Snapshot!.Gross);
        var unsuccessful = await fixture.Read<Invoice>(user.TenantId, successful.Id == left.Id ? right.Id : left.Id);
        Assert.Equal(InvoiceStatus.Draft, unsuccessful.Status); Assert.Null(unsuccessful.Calculation);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Counters.IgnoreQueryFilters().Where(x => x.TenantId == user.TenantId && x.Kind == "RE").Select(x => x.Value).SingleAsync());
        });
    }

    [PostgresApiFact]
    public async Task Two_real_corrections_of_an_adjusted_final_reverse_its_actual_net_tax_and_receivable()
    {
        var (_, client) = await Session(); var order = await Order(client);
        var progress = await Draft(client, order, InvoiceKind.Progress, 1m);
        await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/issue", new { }, progress.Revision));
        var final = await Draft(client, order, InvoiceKind.Final, 2m);
        final = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, final.Revision));
        var first = await Correction(client, final, 1m); var second = await Correction(client, final, 1m);
        Assert.Equal((.01m, .01m), (first.Snapshot!.Net, first.Snapshot.Tax));
        Assert.Equal((.01m, 0m), (second.Snapshot!.Net, second.Snapshot.Tax));
        Assert.Equal(final.Snapshot!.Gross, first.Snapshot.Gross + second.Snapshot.Gross);
        var balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{final.Id}/balance"));
        Assert.Equal(.03m, balance.Credits); Assert.Equal(0m, balance.Outstanding); Assert.Equal(0m, balance.Collectible);
        Assert.Equal(final.IssuedDigest, second.Calculation!.Sources.Single().DocumentDigest);
    }

    [PostgresApiFact]
    public async Task Tampered_jsonb_calculation_is_rejected_by_balance_and_pdf_without_generating_a_document()
    {
        var (user, client) = await Session(); var order = await Order(client);
        var original = await Draft(client, order, InvoiceKind.Final, 3m);
        original = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{original.Id}/issue", new { }, original.Revision));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Id == original.Id && x.Kind == "invoices");
            var invoice = AppJson.Read<Invoice>(row.Data); var calculation = invoice.Calculation!;
            invoice.Calculation = calculation with { Lines = [calculation.Lines[0] with { AfterTax = 9m }] };
            row.Data = AppJson.Serialize(invoice); await db.SaveChangesAsync();
        });
        await Error(client.GetAsync($"/api/v1/invoices/{original.Id}/balance"), HttpStatusCode.BadRequest, "invoice_calculation_changed");
        await Error(Send(client, HttpMethod.Post, $"/api/v1/invoices/{original.Id}/pdf", new { }, original.Revision), HttpStatusCode.BadRequest, "invoice_calculation_changed");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(0, await db.Documents.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.SourceId == original.Id));
        });
    }

    [PostgresApiFact]
    public async Task Clients_cannot_inject_issued_evidence_into_a_draft_or_use_another_tenants_invoice()
    {
        var (_, client) = await Session(); var order = await Order(client);
        var original = await Draft(client, order, InvoiceKind.Progress, 1m);
        original = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{original.Id}/issue", new { }, original.Revision));
        var draft = await Draft(client, order, InvoiceKind.Final, 2m); draft.Calculation = original.Calculation;
        await Error(Send(client, HttpMethod.Put, $"/api/v1/invoices/{draft.Id}", draft, draft.Revision), HttpStatusCode.BadRequest, "command_required");
        var (_, other) = await Session();
        await Error(Send(other, HttpMethod.Post, $"/api/v1/invoices/{original.Id}/issue", new { }, original.Revision), HttpStatusCode.NotFound, "not_found");
    }


    [PostgresApiFact]
    public async Task Related_invoice_history_excludes_other_orders_and_tenants_without_losing_original_credit_notes()
    {
        var (_, client) = await Session();
        var main = await Order(client); var unrelated = await Order(client);
        var first = await Draft(client, main, InvoiceKind.Progress, 1m);
        first = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{first.Id}/issue", new { }, first.Revision));
        var otherProgress = await Draft(client, unrelated, InvoiceKind.Progress, 1m);
        otherProgress = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{otherProgress.Id}/issue", new { }, otherProgress.Revision));
        await Correction(client, otherProgress, 1m);

        var (_, otherClient) = await Session();
        var foreignOrder = await Order(otherClient);
        var foreign = await Draft(otherClient, foreignOrder, InvoiceKind.Progress, 1m);
        await Ok<Invoice>(Send(otherClient, HttpMethod.Post, $"/api/v1/invoices/{foreign.Id}/issue", new { }, foreign.Revision));

        var final = await Draft(client, main, InvoiceKind.Final, 2m);
        final = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, final.Revision));
        Assert.Equal([first.Id], final.Calculation!.PreviousInvoices.Select(x => x.Id).ToArray());
        Assert.Equal((.02m, .01m, .03m), (final.Snapshot!.Net, final.Snapshot.Tax, final.Snapshot.Gross));
        var balance = await Ok<OpenInvoice>(client.GetAsync($"/api/v1/invoices/{first.Id}/balance"));
        Assert.Equal(.01m, balance.Outstanding);
        Assert.Equal(0m, balance.Credits);
        await Error(otherClient.GetAsync($"/api/v1/invoices/{final.Id}/balance"), HttpStatusCode.NotFound, "not_found");
    }

    [PostgresApiFact]
    public async Task An_issued_history_row_with_mismatched_stored_revision_blocks_further_invoicing_atomically()
    {
        var (user, client) = await Session(); var order = await Order(client);
        var progress = await Draft(client, order, InvoiceKind.Progress, 1m);
        progress = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{progress.Id}/issue", new { }, progress.Revision));
        var final = await Draft(client, order, InvoiceKind.Final, 2m);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var row = await db.Entities.IgnoreQueryFilters().SingleAsync(x => x.TenantId == user.TenantId && x.Kind == "invoices" && x.Id == progress.Id);
            var body = AppJson.Read<Invoice>(row.Data);
            body.Revision++;
            row.Data = AppJson.Serialize(body);
            await db.SaveChangesAsync();
        });
        await Error(Send(client, HttpMethod.Post, $"/api/v1/invoices/{final.Id}/issue", new { }, final.Revision),
            HttpStatusCode.BadRequest, "financial_source_metadata");
        var unchanged = await fixture.Read<Invoice>(user.TenantId, final.Id);
        Assert.Equal(InvoiceStatus.Draft, unchanged.Status);
        Assert.Null(unchanged.Calculation);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(1, await db.Counters.IgnoreQueryFilters().Where(x => x.TenantId == user.TenantId && x.Kind == "RE").Select(x => x.Value).SingleAsync());
        });
    }

    private async Task<(ApiTestUser User, HttpClient Client)> Session()
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id); return (user, await fixture.Login(user));
    }

    private static async Task<Order> Order(HttpClient client)
    {
        var customer = await Create(client, "customers", new Customer { Name = "Synthetic cent-rounding customer", Address = "Synthetic address" });
        var building = await Create(client, "buildings", new Building { CustomerId = customer.Id, Name = "Synthetic building", Address = "Synthetic address" });
        var project = await Create(client, "projects", new Project { CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetic rounding project" });
        var quote = await Create(client, "quotes", new Quote { CustomerId = customer.Id, ProjectId = project.Id, Title = "Synthetic 3-cent contract",
            Lines = [new QuoteLine { Description = "Synthetic roofing work", Quantity = 3m, UnitMaterialCost = .01m, VatPercent = 19m }] });
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/freeze", new { }, quote.Revision));
        quote = await Ok<Quote>(Send(client, HttpMethod.Post, $"/api/v1/quotes/{quote.Id}/accept", new AcceptQuoteRequest(quote.IssuedDigest), quote.Revision));
        return await Ok<Order>(Send(client, HttpMethod.Post, $"/api/v1/orders/from-quote/{quote.Id}", new { }, quote.Revision));
    }

    private static async Task<Invoice> Draft(HttpClient client, Order order, InvoiceKind kind, decimal quantity)
    {
        var invoice = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/from-order/{order.Id}", new { kind }, order.Revision));
        invoice.SellerAddress = "Synthetic workshop address"; invoice.Lines[0].Quantity = quantity;
        return await Ok<Invoice>(Send(client, HttpMethod.Put, $"/api/v1/invoices/{invoice.Id}", invoice, invoice.Revision));
    }

    private static async Task<Invoice> Correction(HttpClient client, Invoice original, decimal quantity)
    {
        var credit = await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{original.Id}/correction", new { reason = "Synthetic quantity correction" }, original.Revision));
        credit.Lines[0].Quantity = quantity;
        credit = await Ok<Invoice>(Send(client, HttpMethod.Put, $"/api/v1/invoices/{credit.Id}", credit, credit.Revision));
        return await Ok<Invoice>(Send(client, HttpMethod.Post, $"/api/v1/invoices/{credit.Id}/issue", new { }, credit.Revision));
    }

    private static Task<T> Create<T>(HttpClient client, string kind, T value) where T : TenantEntity
        => Ok<T>(Send(client, HttpMethod.Post, "/api/v1/" + kind, value));

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {json}"); return AppJson.Read<T>(json);
    }

    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    {
        using var response = await pending; var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json); Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
