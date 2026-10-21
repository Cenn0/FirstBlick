using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FirstBlick.Operations;

internal static class KeyProtection
{
    private const string Purpose = "FirstBlick.Operations.RestoreProbe.v1";
    public static ServiceProvider Provider(Settings settings, X509Certificate2? certificate, bool generate = false)
    {
        var services = new ServiceCollection(); services.AddLogging(b => b.ClearProviders()); services.AddScoped<TenantScope>();
        services.AddDbContext<FirstBlickDbContext>(o => o.UseNpgsql(settings.ConnectionString));
        var builder = services.AddDataProtection().SetApplicationName("FirstBlick").PersistKeysToDbContext<FirstBlickDbContext>();
        if (!generate) builder.DisableAutomaticKeyGeneration();
        if (certificate is not null) { builder.ProtectKeysWithCertificate(certificate); builder.UnprotectKeysWithAnyCertificate(certificate); }
        return services.BuildServiceProvider();
    }
    public static async Task<KeyProbe?> CreateProbe(Settings settings, X509Certificate2? certificate)
    {
        await using var db = settings.Db();
        if (!await db.DataProtectionKeys.AnyAsync()) return null;
        await using var provider = Provider(settings, certificate);
        foreach (var key in provider.GetRequiredService<IKeyManager>().GetAllKeys())
            Require.That(key.CreateEncryptor() is not null, "An archived Data Protection key cannot be decrypted with the configured certificate.");
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var protectedValue = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(plaintext);
        return new(protectedValue, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))));
    }
    public static async Task VerifyProbe(Settings settings, X509Certificate2? certificate, KeyProbe? probe)
    {
        if (probe is null) return;
        await using var provider = Provider(settings, certificate);
        var plaintext = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(probe.ProtectedValue);
        Require.That(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))) == probe.PlaintextSha256, "Restored Data Protection keyring cannot decrypt the pre-backup probe.");
    }
}
