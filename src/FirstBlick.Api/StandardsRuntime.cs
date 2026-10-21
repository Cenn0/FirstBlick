using FirstBlick.Infrastructure.Integrations;

namespace FirstBlick.Api;

/// <summary>Trusted resource path is server configuration, never a request parameter. Bundles are verified once and retained read-only in memory.</summary>
public sealed class StandardsRuntime
{
    private readonly Lazy<OfficialValidationBundle> bundle;
    private readonly Lazy<GaebSchemaValidator> gaeb;
    private readonly Lazy<KositInvoiceValidator> invoices;
    public StandardsRuntime(IConfiguration configuration)
    {
        var path = configuration["Standards:Path"] ?? Environment.GetEnvironmentVariable("FIRSTBLICK_STANDARDS_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "standards");
        bundle = new(() => new OfficialValidationBundle(Path.GetFullPath(path)), LazyThreadSafetyMode.ExecutionAndPublication);
        gaeb = new(() => new GaebSchemaValidator(bundle.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        invoices = new(() => new KositInvoiceValidator(bundle.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public GaebSchemaValidator Gaeb => gaeb.Value;
    public KositInvoiceValidator Invoices => invoices.Value;
}
