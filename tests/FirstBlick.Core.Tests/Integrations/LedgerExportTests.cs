using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FirstBlick.Core.Business;
using FirstBlick.Core.Integrations;
using FirstBlick.Core.Tests.Business;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class LedgerExportTests
{
    private static ExportContext Context(int day = 20) => new(Guid.NewGuid(), BusinessFixture.Tenant, BusinessFixture.Actor,
        new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), new(new(2026, 10, day), new(2026, 10, day), "Europe/Berlin"));
    private static List<Dictionary<string, string>> Rows(ExportBundle bundle, string name)
    {
        var records = CsvV1.Parse(bundle.Files.Single(x => x.Name == name).Content, maximumColumns: 128);
        return records.Skip(1).Select(x => records[0].Fields.Zip(x.Fields).ToDictionary(y => y.First, y => y.Second)).ToList();
    }

    [Fact]
    public void Open_receivable_uses_exact_current_credits_cash_discount_and_released_retention_with_linked_evidence()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(47.60m);
        var allocation = PaymentWorkflow.Allocate(invoice, bank, [], [], 47.60m, 1.19m, 11.90m, "Agreed discount and retention", BusinessFixture.Actor, BusinessFixture.Now);
        PaymentWorkflow.ReleaseRetention(allocation, 5.95m, "Partial release", BusinessFixture.Actor, BusinessFixture.Now.AddDays(2));
        var credit = InvoiceWorkflow.DraftCreditNote(invoice, "Correct one fifth"); credit.Lines = [credit.Lines[0] with { Quantity = 0.2m }];
        credit.DueDate = new(2026, 11, 3); InvoiceWorkflow.IssueCreditNote(credit, invoice, [], "CN-2026-1", new(2026, 10, 21));
        var data = new LedgerExportSources([invoice, credit], [], [bank], [allocation], []);
        var result = LedgerExport.OpenItems(Context(), data); var balance = Assert.Single(Rows(result, "open_receivables.csv"));
        Assert.Equal("119.00", balance["gross_eur"]); Assert.Equal("23.80", balance["credits_eur"]);
        Assert.Equal("47.60", balance["paid_eur"]); Assert.Equal("1.19", balance["discount_eur"]);
        Assert.Equal("5.95", balance["retained_eur"]); Assert.Equal("46.41", balance["outstanding_eur"]); Assert.Equal("40.46", balance["collectible_eur"]);
        Assert.Equal("true", balance["overdue"]); Assert.Equal(invoice.IssuedDigest, balance["issued_digest"]); Assert.Equal(64, balance["balance_evidence_digest"].Length);
        var correction = Rows(result, "receivables.csv").Single(x => x["invoice_id"] == credit.Id.ToString());
        Assert.Equal("context", correction["selection"]); Assert.Equal(invoice.Id.ToString(), correction["original_invoice_id"]);
        Assert.Equal("-1", correction["effect_sign"]); Assert.Equal(credit.IssuedDigest, correction["issued_digest"]);
        var payment = Assert.Single(Rows(result, "customer_allocations.csv"));
        Assert.Equal(bank.Id.ToString(), payment["bank_transaction_id"]); Assert.Equal(allocation.PostedDigest, payment["posted_digest"]);
        var release = Assert.Single(Rows(result, "retention_releases.csv"));
        Assert.Equal("5.95", release["amount_eur"]); Assert.Equal("false", release["release_in_window"]); Assert.Equal(allocation.Id.ToString(), release["allocation_id"]);
        Assert.Contains("not a historical as-of", result.Manifest.Selection, StringComparison.Ordinal);
    }

    [Fact]
    public void Reversal_event_window_includes_original_invoice_original_bank_and_return_bank_as_context()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(60);
        var allocation = PaymentWorkflow.Allocate(invoice, bank, [], [], 60, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var returned = BusinessFixture.Bank(-60, "return");
        PaymentWorkflow.ReverseForReturn(allocation, returned, [allocation], "Actual returned transfer", BusinessFixture.Actor, BusinessFixture.Now.AddDays(2));
        var bundle = LedgerExport.Accounting(Context(22), new([invoice], [], [bank, returned], [allocation], []));
        var payment = Assert.Single(Rows(bundle, "customer_allocations.csv"));
        Assert.Equal("window", payment["selection"]); Assert.Equal("false", payment["allocation_in_window"]); Assert.Equal("true", payment["reversal_in_window"]);
        Assert.Equal(returned.Id.ToString(), payment["return_bank_transaction_id"]); Assert.Equal(allocation.PostedDigest, payment["posted_digest"]);
        Assert.Equal("context", Assert.Single(Rows(bundle, "receivables.csv"))["selection"]);
        Assert.Equal(2, Rows(bundle, "bank_transactions.csv").Count);
        var open = LedgerExport.OpenItems(Context(), new([invoice], [], [bank, returned], [allocation], []));
        Assert.Equal("119.00", Assert.Single(Rows(open, "open_receivables.csv"))["outstanding_eur"]);
    }

    [Fact]
    public void Supplier_credit_and_payment_are_reconciled_with_their_original_ids_and_approval_digests()
    {
        var supplier = new Supplier { TenantId = BusinessFixture.Tenant, Name = "@Supplier", Address = "Street 1" };
        var invoice = new SupplierInvoice { TenantId = BusinessFixture.Tenant, SupplierId = supplier.Id, Number = "SI-1", Title = "Material", InvoiceDate = new(2026, 10, 20), DueDate = new(2026, 11, 3), Net = 100, Tax = 19, Gross = 119 };
        PayablesWorkflow.Approve(invoice, supplier, BusinessFixture.Actor, BusinessFixture.Actor, BusinessFixture.Now, false);
        var credit = new SupplierInvoice { TenantId = BusinessFixture.Tenant, SupplierId = supplier.Id, Number = "SC-1", Title = "Material correction", InvoiceDate = new(2026, 10, 21), DueDate = new(2026, 11, 3), Net = 20, Tax = 3.80m, Gross = 23.80m, Kind = SupplierInvoiceKind.CreditNote, OriginalInvoiceId = invoice.Id };
        PayablesWorkflow.ApproveCredit(credit, invoice, supplier, [], BusinessFixture.Actor, BusinessFixture.Actor, BusinessFixture.Now.AddDays(1), false);
        var bank = BusinessFixture.Bank(-47.60m);
        var payment = PayablesWorkflow.Allocate(invoice, bank, [], [], [credit], 47.60m, 1.19m, "Agreed discount", SupplierPaymentDirection.Payment, BusinessFixture.Actor, BusinessFixture.Now);
        var bundle = LedgerExport.OpenItems(Context(), new([], [invoice, credit], [bank], [], [payment]));
        var row = Assert.Single(Rows(bundle, "open_payables.csv"));
        Assert.Equal("46.41", row["outstanding_eur"]); Assert.Equal("23.80", row["credits_eur"]); Assert.Equal("text:@Supplier", row["supplier_name"]);
        Assert.Equal(invoice.ApprovedDigest, row["approved_digest"]);
        var correction = Rows(bundle, "payables.csv").Single(x => x["supplier_invoice_id"] == credit.Id.ToString());
        Assert.Equal(invoice.Id.ToString(), correction["original_invoice_id"]); Assert.Equal("-1", correction["effect_sign"]);
        Assert.Equal(payment.PostedDigest, Assert.Single(Rows(bundle, "supplier_allocations.csv"))["posted_digest"]);
    }

    [Fact]
    public void Settled_originals_are_omitted_from_open_tables_but_evidence_is_retained_and_missing_bank_fails()
    {
        var invoice = BusinessFixture.Invoice(); var bank = BusinessFixture.Bank(119);
        var payment = PaymentWorkflow.Allocate(invoice, bank, [], [], 119, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        var bundle = LedgerExport.OpenItems(Context(), new([invoice], [], [bank], [payment], []));
        Assert.Empty(Rows(bundle, "open_receivables.csv")); Assert.Single(Rows(bundle, "receivables.csv")); Assert.Single(Rows(bundle, "customer_allocations.csv"));
        Assert.Equal("export_reference", Assert.Throws<DomainException>(() => LedgerExport.Accounting(Context(), new([invoice], [], [], [payment], []))).Code);
        var foreign = BusinessFixture.Bank(1, "foreign"); foreign.TenantId = Guid.NewGuid();
        Assert.Equal("export_tenant", Assert.Throws<DomainException>(() => LedgerExport.Accounting(Context(), new([invoice], [], [bank, foreign], [payment], []))).Code);
    }

    [Fact]
    public void Zip_manifest_binds_every_exact_csv_byte_and_does_not_claim_provider_certification()
    {
        var bundle = LedgerExport.Accounting(Context(), new([BusinessFixture.Invoice()], [], [], [], []));
        var bytes = bundle.ToZip(); using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(bundle.Files.Count + 1, archive.Entries.Count); Assert.False(bundle.Manifest.InitiatesPayments); Assert.False(bundle.Manifest.CertifiedProviderFormat);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal(LedgerExport.AccountingSchema, manifest.RootElement.GetProperty("schema").GetString());
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            using var input = archive.GetEntry(file.GetProperty("name").GetString()!)!.Open(); using var output = new MemoryStream(); input.CopyTo(output);
            var content = output.ToArray(); Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(content)));
            Assert.Equal(file.GetProperty("bytes").GetInt32(), content.Length);
            var records = CsvV1.Parse(content, maximumColumns: 128); Assert.Equal(file.GetProperty("rows").GetInt32(), records.Count - 1);
            Assert.All(records.Skip(1), x => Assert.Equal(records[0].Fields.Count, x.Fields.Count));
        }
    }
}
