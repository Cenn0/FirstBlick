using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FirstBlick.Api;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2_097_152);
builder.Services.AddFirstBlickStorage(builder.Configuration);
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(o =>
{
    o.Password.RequiredLength = 14;
    o.User.RequireUniqueEmail = true;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.SignIn.RequireConfirmedEmail = true;
}).AddEntityFrameworkStores<FirstBlickDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = local ? "FirstBlick.Session" : "__Host-FirstBlick.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
var keys = builder.Services.AddDataProtection().SetApplicationName("FirstBlick").PersistKeysToDbContext<FirstBlickDbContext>();
if (!local)
{
    var path = builder.Configuration["DataProtection:CertificatePath"] ?? throw new InvalidOperationException("A data-protection encryption certificate is required outside development/testing.");
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, builder.Configuration["DataProtection:CertificatePassword"]);
    using var key = certificate.GetRSAPrivateKey();
    if (!certificate.HasPrivateKey || key is null) throw new InvalidOperationException("The data-protection certificate must contain an RSA private key.");
    keys.ProtectKeysWithCertificate(certificate);
}
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = local ? "FirstBlick.Antiforgery" : "__Host-FirstBlick.Antiforgery";
    o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
// Keep malformed Minimal API requests on the same bounded problem-details path in every environment.
builder.Services.PostConfigure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddOpenApi("v1");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    o.SerializerOptions.Converters.Add(new DecimalStringConverter());
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    o.SerializerOptions.MaxDepth = 32;
    o.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
});
builder.Services.AddScoped<TenantAccessFilter>();
builder.Services.AddScoped<CommandExecutor>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddSingleton<StandardsRuntime>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    var permits = Math.Clamp(builder.Configuration.GetValue("Authentication:RequestsPerMinute", 20), 1, 1000);
    o.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (args.Contains("--migrate") || args.Contains("--bootstrap"))
{
    await using var serviceScope = app.Services.CreateAsyncScope();
    var db = serviceScope.ServiceProvider.GetRequiredService<FirstBlickDbContext>();
    await db.Database.MigrateAsync();
    if (args.Contains("--bootstrap")) await Bootstrap.Run(serviceScope.ServiceProvider, builder.Configuration);
    return;
}
app.UseMiddleware<ErrorMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(self)";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'; font-src 'self'; object-src 'none'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
        if (!local && context.Request.IsHttps) context.Response.Headers.StrictTransportSecurity = "max-age=2592000";
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store, private";
        return Task.CompletedTask;
    });
    await next();
});
if (!local) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        await RequestFingerprint.Capture(context);
    }
    await next();
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (FirstBlickDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
AuthEndpoints.Map(app);
var api = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<TenantAccessFilter>();
BusinessEndpoints.Map(api);
ManagementEndpoints.Map(api);
DocumentEndpoints.Map(api);
TechnicalEndpoints.Map(api);
PortalEndpoints.Map(api);
IntegrationEndpoints.Map(api);
TradePreviewEndpoints.Map(api);
GaebCatalogEndpoints.Map(api);
OfficialInvoiceEndpoints.Map(api);
OfficialGaebExchangeEndpoints.Map(api);
QuantityEndpoints.Map(api);
CostEndpoints.Map(api);
SupplierCostAllocationEndpoints.Map(api);
DiscoveryEndpoints.Map(api);
OperationsEndpoints.Map(api);
CollectionEndpoints.Map(api);
app.MapOpenApi().RequireAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
