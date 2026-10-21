using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Explicit historical fixtures for the isolated browser-test database. This is not an API feature.
var connectionString = Required("ConnectionStrings__Database");
var connection = new NpgsqlConnectionStringBuilder(connectionString);
if (connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
    throw new InvalidOperationException("The historical collection fixture runner requires a loopback test database.");
if (connection.Database?.StartsWith("firstblick_live_", StringComparison.Ordinal) != true)
    throw new InvalidOperationException("The historical collection fixture runner requires a database named firstblick_live_*.");
var email = Required("FIRSTBLICK_LIVE_EMAIL");
if (!email.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The historical collection fixture runner requires a synthetic operator at example.test.");
var outputPath = Path.GetFullPath(Required("FIRSTBLICK_COLLECTION_FIXTURES_PATH"));
if (File.Exists(outputPath)) throw new InvalidOperationException("The fixture manifest already exists. Use a fresh path for another synthetic run.");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var scope = new TenantScope();
await using var db = new FirstBlickDbContext(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(connectionString).Options, scope);
var user = await db.Users.SingleAsync(x => x.NormalizedEmail == email.ToUpperInvariant());
if (user.Email?.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase) != true)
    throw new InvalidOperationException("The stored fixture operator must use a synthetic example.test address.");
if (!user.EmailConfirmed || (user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow))
    throw new InvalidOperationException("The synthetic operator must have a confirmed available account.");
var memberships = await db.Memberships.Where(x => x.UserId == user.Id && x.Active && x.Role == BusinessRole.ManagingDirector).ToListAsync();
var selectedTenant = Environment.GetEnvironmentVariable("FIRSTBLICK_LIVE_TENANT_ID");
var membership = string.IsNullOrWhiteSpace(selectedTenant) ? memberships.Single() : memberships.Single(x => x.TenantId == Guid.Parse(selectedTenant));
var tenant = await db.Tenants.SingleAsync(x => x.Id == membership.TenantId);
if (!tenant.Modules.Contains("finance")) throw new InvalidOperationException("The isolated test business must enable finance.");
scope.TenantId = tenant.Id; scope.UserId = user.Id; scope.Role = membership.Role;
var now = DateTimeOffset.UtcNow;
var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime);
var store = new EntityStore(db, scope);
await using var transaction = await db.Database.BeginTransactionAsync();
await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenant.Id.ToString()},0))");
var fixtures = new Dictionary<string, object>(StringComparer.Ordinal);
foreach (var viewport in new[] { "desktop", "tablet", "mobile" })
{
    var suffix = Guid.NewGuid().ToString("N")[..10];
    var customer = await Save(new Customer { TenantId = tenant.Id, Name = $"Synthetischer Forderungskunde {viewport} {suffix}", Address = "Synthetischer Testweg 1" });
    var building = await Save(new Building { TenantId = tenant.Id, CustomerId = customer.Id, Name = "Historisches Testdach " + suffix, Address = "Synthetischer Testweg 1" });
    var project = await Save(new Project { TenantId = tenant.Id, CustomerId = customer.Id, BuildingId = building.Id, Name = "Synthetischer Forderungsfall " + suffix });
    var quote = new Quote
    {
        TenantId = tenant.Id, CustomerId = customer.Id, ProjectId = project.Id, Title = "Historisches synthetisches Angebot " + suffix,
        Lines = [new() { Description = "Synthetische Dacharbeit", UnitMaterialCost = 100m, VatPercent = 19m }]
    };
    CommercialWorkflow.IssueQuote(quote, "Q-COL-" + suffix);
    CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, now.AddDays(-31), day.AddDays(-31));
    await Save(quote);
    var order = await Save(CommercialWorkflow.CreateOrder(quote, "O-COL-" + suffix));
    var invoice = InvoiceWorkflow.DraftFromOrder(order, customer);
    invoice.SellerName = "Synthetischer Testbetrieb"; invoice.SellerAddress = "Synthetischer Testweg 2"; invoice.SellerTaxId = "SYNTHETIC-TEST-ID";
    invoice.ServiceFrom = day.AddDays(-31); invoice.ServiceUntil = day.AddDays(-30); invoice.DueDate = day.AddDays(-15);
    InvoiceWorkflow.IssueForOrder(invoice, order, [], [], "RE-COL-" + suffix, day.AddDays(-30));
    await Save(invoice);
    fixtures.Add(viewport, new { invoice.Id, invoice.Number, invoice.IssuedDigest, invoice.Revision, invoice.IssueDate, invoice.DueDate,
        Gross = invoice.Snapshot!.Gross, ProjectId = project.Id, OrderId = order.Id });
}
await db.SaveChangesAsync();
await transaction.CommitAsync();
var manifest = new
{
    Schema = "firstblick.collection-browser-fixtures/1.0.0", Synthetic = true, CapturedAt = now, BusinessDate = day,
    SetupMethod = "Historical synthetic invoices created directly through Core InvoiceWorkflow.IssueForOrder and EntityStore in an isolated database. No public issuance endpoint was backdated.",
    TenantId = tenant.Id, UserId = user.Id, Invoices = fixtures
};
await using (var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
await using (var writer = new StreamWriter(file)) await writer.WriteAsync(AppJson.Serialize(manifest));
Console.WriteLine($"Created 3 historical synthetic collection invoices and manifest: {outputPath}");

async Task<T> Save<T>(T entity) where T : TenantEntity
{
    await store.Save(entity, "test.synthetic_historical_collection", true);
    return entity;
}
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name} for the isolated historical collection fixture runner.");
