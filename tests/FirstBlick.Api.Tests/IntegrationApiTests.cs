using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FirstBlick.Api.Tests;

public sealed class IntegrationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private const string Root = "/api/v1/integrations/v1";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static byte[] Csv(params string[] rows) => CsvV1.Utf8.GetBytes(string.Join(',', BankCsvV1.Header) + "\r\n" + string.Join("\r\n", rows) + "\r\n");
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Task<HttpResponseMessage> Bank(HttpClient client, string command, byte[] bytes, string? key = null, string? digest = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Root + "/bank-csv/" + command) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
        if (command == "import")
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
            if (digest is not null) request.Headers.Add("X-Import-SHA256", digest);
        }
        return client.SendAsync(request);
    }
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return AppJson.Read<T>(text);
    }
    private static async Task Error(Task<HttpResponseMessage> pending, HttpStatusCode status, string code)
    {
        using var response = await pending; var text = await response.Content.ReadAsStringAsync(); Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(text); Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }
    private async Task<(ApiTestUser User, HttpClient Client)> Session(BusinessRole role = BusinessRole.ManagingDirector)
    {
        var tenant = await fixture.CreateTenant(); var user = await fixture.CreateUser(tenant.Id, role); return (user, await fixture.Login(user));
    }
    private async Task<int> Count(Guid tenant, string kind) => await fixture.InScope(async services =>
        await services.GetRequiredService<FirstBlickDbContext>().Entities.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && x.Kind == kind));

    [PostgresApiFact]
    public async Task Csv_preview_is_read_only_and_import_is_atomic_idempotent_and_bound_to_actual_raw_bytes()
    {
        var (user, client) = await Session();
        var source = Csv("account-a,ext-1,2026-10-20,12.50,EUR,\"Customer, One\",Invoice 1", "account-a,ext-1,2026-10-20,12.50,EUR,\"Customer, One\",Invoice 1", "account-a,ext-2,2026-10-20,-5.00,EUR,=Untrusted text,Debit");
        var preview = await Ok<BankCsvPreview>(Bank(client, "preview", source));
        Assert.Equal(Sha(source), preview.SourceSha256); Assert.True(preview.CanImport); Assert.Equal(2, preview.New); Assert.Equal(1, preview.Reused);
        Assert.Equal(0, await Count(user.TenantId, "bank-transactions"));
        await Error(Bank(client, "import", source), HttpStatusCode.BadRequest, "preview_mismatch");
        await Error(Bank(client, "import", source, digest: new string('0', 64)), HttpStatusCode.BadRequest, "preview_mismatch");
        var key = Guid.NewGuid().ToString("N"); var result = await Ok<BankImportReceipt>(Bank(client, "import", source, key, preview.SourceSha256));
        Assert.Equal(2, result.Created); Assert.Equal(1, result.Reused); Assert.Equal(result.Items[0].Id, result.Items[1].Id);
        Assert.Equal(user.TenantId, result.Event.TenantId); Assert.Equal(user.UserId, result.Event.ActorId); Assert.Equal("bank-import.completed", result.Event.Type);
        using var replayResponse = await Bank(client, "import", source, key, Sha(source));
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode); Assert.Equal("true", replayResponse.Headers.GetValues("Idempotency-Replayed").Single());
        var replay = AppJson.Read<BankImportReceipt>(await replayResponse.Content.ReadAsStringAsync()); Assert.Equal(result.Event.Id, replay.Event.Id);
        var repeated = await Ok<BankImportReceipt>(Bank(client, "import", source, digest: Sha(source)));
        Assert.Equal(0, repeated.Created); Assert.Equal(3, repeated.Reused); Assert.Equal(result.Items.Select(x => x.Id), repeated.Items.Select(x => x.Id));
        var changed = Csv("account-a,ext-3,2026-10-20,1.00,EUR,Other,Reference");
        await Error(Bank(client, "import", changed, key, Sha(changed)), HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(2, await Count(user.TenantId, "bank-transactions")); Assert.Equal(0, await Count(user.TenantId, "payment-allocations"));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            Assert.Equal(2, await db.Jobs.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Type == "entity.changed"));
            Assert.Equal(2, await db.Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId));
            Assert.Equal(2, await db.Audit.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId && x.Action == "adapter.bank-csv.completed"));
        });
    }

    [PostgresApiFact]
    public async Task Any_duplicate_conflict_or_invalid_final_row_prevents_all_rows_and_receipts_from_being_written()
    {
        var (user, client) = await Session(); var original = Csv("a,original,2026-10-20,10.00,EUR,Name,Ref");
        await Ok<BankImportReceipt>(Bank(client, "import", original, digest: Sha(original)));
        var conflict = Csv("a,new-valid,2026-10-20,3.00,EUR,Name,Ref", "a,original,2026-10-20,11.00,EUR,Name,Ref");
        var preview = await Ok<BankCsvPreview>(Bank(client, "preview", conflict)); Assert.False(preview.CanImport); Assert.Equal(1, preview.New); Assert.Equal(1, preview.Conflicts);
        await Error(Bank(client, "import", conflict, digest: Sha(conflict)), HttpStatusCode.Conflict, "idempotency_conflict");
        var malformed = Csv("a,new-valid,2026-10-20,3.00,EUR,Name,Ref", "a,invalid,2026-10-20,1.001,EUR,Name,Ref");
        await Error(Bank(client, "import", malformed, digest: Sha(malformed)), HttpStatusCode.BadRequest, "csv_amount");
        Assert.Equal(1, await Count(user.TenantId, "bank-transactions"));
        await fixture.InScope(async services => Assert.Equal(1, await services.GetRequiredService<FirstBlickDbContext>().Receipts.IgnoreQueryFilters().CountAsync(x => x.TenantId == user.TenantId)));
    }

    [PostgresApiFact]
    public async Task Concurrent_imports_serialize_the_same_account_external_key_without_duplicate_bank_evidence()
    {
        var (user, client) = await Session(); var source = Csv("a,concurrent,2026-10-20,10.00,EUR,Name,Ref");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Ok<BankImportReceipt>(Bank(client, "import", source, digest: Sha(source)))));
        Assert.Equal(1, results.Sum(x => x.Created)); Assert.Equal(5, results.Sum(x => x.Reused));
        Assert.Single(results.SelectMany(x => x.Items).Select(x => x.Id).Distinct()); Assert.Equal(1, await Count(user.TenantId, "bank-transactions"));
    }

    [PostgresApiFact]
    public async Task Bank_adapter_enforces_current_finance_role_module_and_tenant_isolation()
    {
        var (user, client) = await Session(BusinessRole.Accounting); var source = Csv("same-account,same-id,2026-10-20,7.00,EUR,Visible only here,Ref");
        var first = await Ok<BankImportReceipt>(Bank(client, "import", source, digest: Sha(source)));
        var (other, otherClient) = await Session(BusinessRole.Accounting);
        var preview = await Ok<BankCsvPreview>(Bank(otherClient, "preview", source)); Assert.Equal(1, preview.New); Assert.Null(preview.Rows.Single().ExistingId);
        var second = await Ok<BankImportReceipt>(Bank(otherClient, "import", source, digest: Sha(source)));
        Assert.NotEqual(first.Items.Single().Id, second.Items.Single().Id); Assert.NotEqual(first.Items.Single().ImportDigest, second.Items.Single().ImportDigest);
        var office = await fixture.CreateUser(user.TenantId, BusinessRole.Office); var officeClient = await fixture.Login(office);
        await Error(Bank(officeClient, "preview", source), HttpStatusCode.Forbidden, "forbidden");
        await Error(Bank(officeClient, "import", source, digest: Sha(source)), HttpStatusCode.Forbidden, "forbidden");
        var key = Guid.NewGuid().ToString("N"); await Ok<BankImportReceipt>(Bank(client, "import", source, key, Sha(source)));
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>();
            var membership = await db.Memberships.SingleAsync(x => x.TenantId == user.TenantId && x.UserId == user.UserId); membership.Role = BusinessRole.Office; await db.SaveChangesAsync();
        });
        await Error(Bank(client, "import", source, key, Sha(source)), HttpStatusCode.Forbidden, "forbidden");
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == other.TenantId);
            tenant.Modules = ["core", "commercial"]; await db.SaveChangesAsync();
        });
        await Error(Bank(otherClient, "preview", source), HttpStatusCode.Forbidden, "module_disabled");
        await Error(Bank(otherClient, "import", source, digest: Sha(source)), HttpStatusCode.Forbidden, "module_disabled");
    }

    [PostgresApiFact]
    public async Task Finance_exports_include_connected_current_corrections_payments_and_digest_manifest_without_other_tenants()
    {
        var (user, client) = await Session(); var (invoice, _) = await SeedInvoice(user);
        var bank = new BankTransaction { TenantId = user.TenantId, AccountReference = "account", ExternalId = "payment", BookedOn = new(2026, 10, 20), Amount = 47.60m, Counterparty = "=Customer input", Reference = "=1+1" };
        PaymentWorkflow.SealImport(bank); await fixture.Save(bank, user.UserId);
        var payment = PaymentWorkflow.Allocate(invoice, bank, [], [], 47.60m, 1.19m, 11.90m, "Agreed deduction", user.UserId, Now);
        PaymentWorkflow.ReleaseRetention(payment, 5.95m, "Later release", user.UserId, Now.AddDays(2)); await fixture.Save(payment, user.UserId);
        var credit = InvoiceWorkflow.DraftCreditNote(invoice, "Later correction"); credit.Lines = [credit.Lines[0] with { Quantity = 0.2m }]; credit.DueDate = new(2026, 11, 3);
        InvoiceWorkflow.IssueCreditNote(credit, invoice, [], "CN-1", new(2026, 10, 21)); await fixture.Save(credit, user.UserId);
        var (foreignUser, _) = await Session(); var (foreignInvoice, _) = await SeedInvoice(foreignUser, "FOREIGN-CUSTOMER-DO-NOT-EXPORT");
        var accounting = await Export(client, "accounting"); var open = await Export(client, "open-items");
        var invoices = Table(accounting, "receivables.csv"); Assert.Equal(2, invoices.Count);
        Assert.Equal("context", invoices.Single(x => x["invoice_id"] == credit.Id.ToString())["selection"]);
        Assert.Equal(invoice.Id.ToString(), invoices.Single(x => x["invoice_id"] == credit.Id.ToString())["original_invoice_id"]);
        Assert.Equal("text:=1+1", Assert.Single(Table(accounting, "bank_transactions.csv"))["reference"]);
        var balance = Assert.Single(Table(open, "open_receivables.csv")); Assert.Equal("46.41", balance["outstanding_eur"]); Assert.Equal("40.46", balance["collectible_eur"]);
        Assert.Equal("5.95", balance["retained_eur"]); Assert.Equal("23.80", balance["credits_eur"]); Assert.Equal(invoice.IssuedDigest, balance["issued_digest"]);
        foreach (var file in accounting.Values.Concat(open.Values))
        {
            var text = CsvV1.Utf8.GetString(file); Assert.DoesNotContain(foreignInvoice.Id.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("FOREIGN-CUSTOMER-DO-NOT-EXPORT", text, StringComparison.Ordinal);
        }
        using var manifest = JsonDocument.Parse(open["manifest.json"]);
        Assert.Equal(user.TenantId.ToString(), manifest.RootElement.GetProperty("capture").GetProperty("tenantId").GetString());
        Assert.Contains("current complete posted state", manifest.RootElement.GetProperty("selection").GetString(), StringComparison.Ordinal);
        await Error(client.GetAsync(Root + "/exports/open-items?from=2026-10-20&to=2026-10-20&asOf=2026-10-20"), HttpStatusCode.BadRequest, "unsupported_as_of");
        await Error(client.GetAsync(Root + "/exports/accounting?from=2026-01-01&to=2027-01-02"), HttpStatusCode.BadRequest, "export_window");
    }

    [PostgresApiFact]
    public async Task Payroll_http_export_uses_business_midnights_dst_breaks_and_only_approved_records()
    {
        var (user, client) = await Session(); var (_, project) = await SeedInvoice(user);
        await fixture.InScope(async services =>
        {
            var db = services.GetRequiredService<FirstBlickDbContext>(); var tenant = await db.Tenants.SingleAsync(x => x.Id == user.TenantId); tenant.TimeZone = "Europe/Berlin"; await db.SaveChangesAsync();
        });
        var employee = await fixture.Save(new Employee { TenantId = user.TenantId, Name = "=Approved employee", Active = false }, user.UserId);
        var entry = new TimeEntry { TenantId = user.TenantId, EmployeeId = employee.Id, ProjectId = project.Id, Activity = "=Recorded work", Approved = true,
            Start = new(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), End = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero),
            Breaks = [new(new(2026, 10, 24, 21, 45, 0, TimeSpan.Zero), new(2026, 10, 24, 22, 15, 0, TimeSpan.Zero)),
                new(new(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), new(2026, 10, 25, 1, 30, 0, TimeSpan.Zero))] };
        await fixture.Save(entry, user.UserId);
        await fixture.Save(new TimeEntry { TenantId = user.TenantId, EmployeeId = employee.Id, ProjectId = project.Id, Activity = "UNAPPROVED-DO-NOT-EXPORT", Start = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), End = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero) }, user.UserId);
        var archive = await Export(client, "payroll-time", "2026-10-25", "2026-10-25"); var row = Assert.Single(Table(archive, "approved_time.csv"));
        Assert.Equal(entry.Id.ToString(), row["time_entry_id"]); Assert.Equal("2026-10-25", row["local_date"]);
        Assert.Equal("3.75", row["net_hours"]); Assert.Equal((5 * TimeSpan.TicksPerHour).ToString(), row["gross_ticks"]);
        Assert.Equal(TimeSpan.FromMinutes(75).Ticks.ToString(), row["break_ticks"]); Assert.Equal("false", row["employee_active_current"]);
        Assert.Equal("text:=Approved employee", row["employee_name_current"]);
        Assert.DoesNotContain("UNAPPROVED-DO-NOT-EXPORT", CsvV1.Utf8.GetString(archive["approved_time.csv"]), StringComparison.Ordinal);
    }

    [PostgresApiFact]
    public async Task Bulk_exports_enforce_the_explicit_finance_and_payroll_role_matrix()
    {
        var tenant = await fixture.CreateTenant();
        foreach (var role in Enum.GetValues<BusinessRole>())
        {
            var user = await fixture.CreateUser(tenant.Id, role); var client = await fixture.Login(user);
            var financeAllowed = role is BusinessRole.ManagingDirector or BusinessRole.Office or BusinessRole.Accounting;
            var payrollAllowed = role is BusinessRole.ManagingDirector or BusinessRole.Office;
            foreach (var kind in new[] { "accounting", "open-items", "payroll-time" })
            {
                using var response = await client.GetAsync(Root + $"/exports/{kind}?from=2026-10-20&to=2026-10-20");
                Assert.Equal(kind == "payroll-time" ? payrollAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden : financeAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> Export(HttpClient client, string kind, string from = "2026-10-20", string to = "2026-10-20")
    {
        using var response = await client.GetAsync(Root + $"/exports/{kind}?from={from}&to={to}"); var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? null : $"{response.StatusCode}: {CsvV1.Utf8.GetString(bytes)}");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType); Assert.Equal(Sha(bytes), response.Headers.GetValues("X-Content-SHA256").Single());
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); var files = new Dictionary<string, byte[]>();
        foreach (var item in archive.Entries)
        {
            using var source = item.Open(); using var target = new MemoryStream(); await source.CopyToAsync(target); files.Add(item.FullName, target.ToArray());
        }
        using var manifest = JsonDocument.Parse(files["manifest.json"]);
        Assert.False(manifest.RootElement.GetProperty("initiatesPayments").GetBoolean()); Assert.False(manifest.RootElement.GetProperty("certifiedProviderFormat").GetBoolean());
        Assert.Equal(manifest.RootElement.GetProperty("schema").GetString(), response.Headers.GetValues("X-FirstBlick-Export-Schema").Single());
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var content = files[item.GetProperty("name").GetString()!]; Assert.Equal(item.GetProperty("sha256").GetString(), Sha(content));
            Assert.Equal(item.GetProperty("bytes").GetInt32(), content.Length); var records = CsvV1.Parse(content, maximumColumns: 128);
            Assert.Equal(item.GetProperty("rows").GetInt32(), records.Count - 1);
        }
        return files;
    }
    private static List<Dictionary<string, string>> Table(Dictionary<string, byte[]> files, string name)
    {
        var records = CsvV1.Parse(files[name], maximumColumns: 128);
        return records.Skip(1).Select(x => records[0].Fields.Zip(x.Fields).ToDictionary(y => y.First, y => y.Second)).ToList();
    }
    private async Task<(Invoice Invoice, Project Project)> SeedInvoice(ApiTestUser person, string name = "Synthetic export customer")
    {
        var customer = await fixture.Save(new Customer { TenantId = person.TenantId, Name = name, Address = "Synthetic Street 1" }, person.UserId);
        var building = await fixture.Save(new Building { TenantId = person.TenantId, CustomerId = customer.Id, Name = "Roof", Address = "Synthetic Street 1" }, person.UserId);
        var project = await fixture.Save(new Project { TenantId = person.TenantId, CustomerId = customer.Id, BuildingId = building.Id, Name = "Export site" }, person.UserId);
        var quote = new Quote { TenantId = person.TenantId, CustomerId = customer.Id, ProjectId = project.Id, Title = "Roof work", Lines = [new() { Description = "Roof work", Quantity = 1, UnitMaterialCost = 100, VatPercent = 19 }] };
        CommercialWorkflow.IssueQuote(quote, "Q-1"); CommercialWorkflow.FreezePresentation(quote, "Synthetic business", customer, Now);
        CommercialWorkflow.AcceptQuote(quote, quote.IssuedDigest, Now, new(2026, 10, 20)); await fixture.Save(quote, person.UserId);
        var order = CommercialWorkflow.CreateOrder(quote, "O-1"); await fixture.Save(order, person.UserId);
        var invoice = InvoiceWorkflow.DraftFromOrder(order, customer, InvoiceKind.Final); invoice.SellerName = "Synthetic business"; invoice.SellerAddress = "Workshop 2";
        invoice.ServiceFrom = new(2026, 10, 1); invoice.ServiceUntil = new(2026, 10, 20); invoice.DueDate = new(2026, 11, 3);
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "I-1", new(2026, 10, 20)); await fixture.Save(invoice, person.UserId); return (invoice, project);
    }
}
