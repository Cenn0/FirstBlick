namespace FirstBlick.Core.Security;

public enum BusinessRole { ManagingDirector, Office, Accounting, SiteManager, Worker, Trainer, Partner }

public static class AccessRules
{
    public static readonly string[] AllModules = ["core", "commercial", "field", "purchasing", "finance", "maintenance", "training", "technical", "portal"];
    public static readonly string[] DefaultModules = ["core", "commercial", "field", "purchasing", "finance", "maintenance", "technical"];
    public static string ModuleFor(string kind) => kind switch
    {
        "quotes" or "orders" or "change-orders" or "catalog-items" => "commercial",
        "invoices" or "bank-transactions" or "payment-allocations" => "finance",
        "suppliers" or "purchase-orders" or "materials" or "stock-movements" or "material-reservations" or "equipment" => "purchasing",
        "employees" or "time-entries" or "absences" or "schedule-assignments" or "diary-entries" or "defects" or "acceptances" or "form-definitions" or "form-submissions" => "field",
        "training-records" => "training",
        "maintenance-contracts" or "notification-preferences" => "maintenance",
        "roof-revisions" or "measurements" or "technical-results" => "technical",
        _ => "core"
    };

    public static bool CanRead(BusinessRole role, string kind) => role switch
    {
        BusinessRole.ManagingDirector => true,
        BusinessRole.Office => kind is not "training-records",
        BusinessRole.Accounting => ModuleFor(kind) is "core" or "commercial" or "finance" or "purchasing",
        BusinessRole.SiteManager => ModuleFor(kind) is "core" or "commercial" or "field" or "purchasing" or "maintenance" or "technical",
        BusinessRole.Worker => kind is "projects" or "buildings" or "tasks" or "employees" or "training-records" or "time-entries" or "schedule-assignments" or "diary-entries" or "defects" or "materials" or "equipment" or "form-definitions" or "form-submissions" or "notification-preferences",
        BusinessRole.Trainer => kind is "projects" or "buildings" or "tasks" or "employees" or "training-records" or "notification-preferences",
        BusinessRole.Partner => false,
        _ => false
    };

    public static bool CanWrite(BusinessRole role, string kind) => role switch
    {
        BusinessRole.ManagingDirector => true,
        BusinessRole.Office => kind is not "bank-transactions" and not "payment-allocations" and not "training-records" and not "employees",
        BusinessRole.Accounting => ModuleFor(kind) is "finance" or "purchasing",
        BusinessRole.SiteManager => ModuleFor(kind) is "field" or "purchasing" or "technical" || kind is "projects" or "buildings" or "tasks" or "maintenance-contracts",
        BusinessRole.Worker => kind is "time-entries" or "diary-entries" or "defects" or "training-records" or "form-submissions" or "notification-preferences",
        BusinessRole.Trainer => kind is "training-records" or "notification-preferences",
        _ => false
    };

    public static void ValidateModules(IEnumerable<string> modules)
    {
        var chosen = modules.ToHashSet(StringComparer.Ordinal);
        if (!chosen.Contains("core") || chosen.Any(x => !AllModules.Contains(x)))
            throw new Business.DomainException("invalid_modules", "The core module is required and every module must be known.");
        if (chosen.Contains("finance") && !chosen.Contains("commercial"))
            throw new Business.DomainException("module_dependency", "Finance requires commercial workflows.");
        if (chosen.Contains("training") && !chosen.Contains("field"))
            throw new Business.DomainException("module_dependency", "Training requires the field-work module.");
    }
}
