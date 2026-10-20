using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirstBlick.Core.Business;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record EntityReference(string Kind, Guid Id);

public abstract class TenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Archived { get; set; }
    public abstract void Validate();
    public virtual IEnumerable<EntityReference> References() => [];

    protected void ValidateCommon()
    {
        Guard.Id(Id, nameof(Id));
        Guard.Id(TenantId, nameof(TenantId));
        Guard.Require(Revision > 0, "invalid_revision", "Revision must be positive.");
        Guard.Utc(CreatedAt, nameof(CreatedAt));
        Guard.Utc(UpdatedAt, nameof(UpdatedAt));
        Guard.Require(UpdatedAt >= CreatedAt, "invalid_time", "UpdatedAt cannot precede CreatedAt.");
    }
}

public static class Guard
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new DomainException(code, message);
    }

    public static void Id(Guid value, string field) => Require(value != Guid.Empty, "required", $"{field} is required.");
    public static void OptionalId(Guid? value, string field)
    {
        if (value.HasValue) Id(value.Value, field);
    }

    public static void Text(string? value, string field, int max = 240, bool required = true)
    {
        Require(!required || !string.IsNullOrWhiteSpace(value), "required", $"{field} is required.");
        Require(value is null || value.Length <= max, "text_too_long", $"{field} exceeds {max} characters.");
        Require(value is null || !value.Contains('\0'), "invalid_text", $"{field} contains a null character.");
    }

    public static void Range(decimal value, decimal min, decimal max, string field)
    {
        Require(value >= min && value <= max, "out_of_range", $"{field} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
        Require(decimal.Round(value, 6) == value, "invalid_precision", $"{field} must have at most six decimal places.");
    }

    public static void Utc(DateTimeOffset value, string field) =>
        Require(value != default && value.Offset == TimeSpan.Zero, "invalid_utc", $"{field} must be a non-empty UTC timestamp.");

    public static void Window(DateTimeOffset start, DateTimeOffset end)
    {
        Utc(start, "Start");
        Utc(end, "End");
        Require(end > start, "invalid_window", "End must be later than start.");
    }

    public static void Items<T>(IReadOnlyCollection<T>? values, string field, int max = 1000, int min = 0)
    {
        Require(values is not null && values.Count >= min && values.Count <= max, "invalid_collection", $"{field} must contain between {min} and {max} items.");
        Require(values!.All(x => x is not null), "invalid_collection", $"{field} cannot contain null items.");
    }

    public static void Currency(string? value) => Require(value == "EUR", "unsupported_currency", "This release supports EUR only.");
    public static void Email(string? value, bool required = false)
    {
        Text(value, "Email", 254, required);
        if (string.IsNullOrEmpty(value)) return;
        Require(System.Net.Mail.MailAddress.TryCreate(value, out var parsed) && parsed.Address == value,
            "invalid_email", "Email must be a plain email address.");
    }
}

public static class Money
{
    public const decimal Maximum = 1_000_000_000_000m;
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public static void Amount(decimal value, string field, bool allowNegative = false)
    {
        Guard.Range(value, allowNegative ? -Maximum : 0, Maximum, field);
        Guard.Require(value == Round(value), "invalid_money_precision", $"{field} must have at most two decimal places.");
    }
}

public static class ContentDigest
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Of<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));
    public static string OfUtf8(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Stable API resource names. All reference resolution must use the same tenant in the database transaction.</summary>
public static class BusinessRegistry
{
    public static IReadOnlyDictionary<string, Type> Entities { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["customers"] = typeof(Customer), ["buildings"] = typeof(Building), ["projects"] = typeof(Project),
        ["tasks"] = typeof(TaskItem), ["catalog-items"] = typeof(CatalogItem), ["quotes"] = typeof(Quote),
        ["orders"] = typeof(Order), ["change-orders"] = typeof(ChangeOrder), ["invoices"] = typeof(Invoice),
        ["bank-transactions"] = typeof(BankTransaction), ["payment-allocations"] = typeof(PaymentAllocation),
        ["suppliers"] = typeof(Supplier), ["purchase-orders"] = typeof(PurchaseOrder), ["materials"] = typeof(Material),
        ["stock-movements"] = typeof(StockMovement), ["material-reservations"] = typeof(MaterialReservation),
        ["employees"] = typeof(Employee), ["time-entries"] = typeof(TimeEntry), ["absences"] = typeof(Absence),
        ["schedule-assignments"] = typeof(ScheduleAssignment), ["diary-entries"] = typeof(DiaryEntry),
        ["defects"] = typeof(Defect), ["acceptances"] = typeof(Acceptance), ["maintenance-contracts"] = typeof(MaintenanceContract),
        ["notification-preferences"] = typeof(NotificationPreference), ["training-records"] = typeof(TrainingRecord),
        ["form-definitions"] = typeof(FormDefinition), ["form-submissions"] = typeof(FormSubmission),
        ["equipment"] = typeof(Equipment), ["supplier-invoices"] = typeof(SupplierInvoice),
        ["supplier-payment-allocations"] = typeof(SupplierPaymentAllocation)
    };

    public static string KindOf<T>() where T : TenantEntity => Entities.Single(x => x.Value == typeof(T)).Key;
    public static EntityReference Reference<T>(Guid id) where T : TenantEntity => new(KindOf<T>(), id);
}
