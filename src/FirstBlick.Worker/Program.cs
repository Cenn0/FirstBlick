using System.Security.Cryptography.X509Certificates;
using FirstBlick.Infrastructure;
using FirstBlick.Infrastructure.Persistence;
using FirstBlick.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFirstBlickStorage(builder.Configuration);
builder.Services.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<FirstBlickDbContext>().AddDefaultTokenProviders();
var keys = builder.Services.AddDataProtection().SetApplicationName("FirstBlick").PersistKeysToDbContext<FirstBlickDbContext>();
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    var path = builder.Configuration["DataProtection:CertificatePath"]
               ?? throw new InvalidOperationException("A data-protection encryption certificate is required outside development/testing.");
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, builder.Configuration["DataProtection:CertificatePassword"]);
    using var privateKey = certificate.GetRSAPrivateKey();
    if (privateKey is null) throw new InvalidOperationException("The data-protection certificate must contain its RSA private key.");
    keys.ProtectKeysWithCertificate(certificate);
}
builder.Services.AddHostedService<OutboxWorker>();
await builder.Build().RunAsync();
