using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using FirstBlick.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Api;

public static class Bootstrap
{
    public static async Task Run(IServiceProvider services, IConfiguration config)
    {
        var email = config["Bootstrap:Email"] ?? throw new InvalidOperationException("Bootstrap:Email is required.");
        var password = config["Bootstrap:Password"] ?? throw new InvalidOperationException("Bootstrap:Password is required.");
        var name = config["Bootstrap:TenantName"] ?? throw new InvalidOperationException("Bootstrap:TenantName is required.");
        Guard.Email(email, true); Guard.Text(name, "TenantName", 200);
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var db = services.GetRequiredService<FirstBlickDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('FirstBlick.Bootstrap', 0))");
        Guard.Require(!await db.Tenants.AnyAsync(), "bootstrap_complete", "The initial business already exists. Use a managing-director invitation to add people.");
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true,
            DisplayName = config["Bootstrap:DisplayName"] ?? "Administrator" };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new DomainException("invalid_account", string.Join(" ", result.Errors.Select(x => x.Description)));
        var tenant = new BusinessTenant { Name = name };
        db.Tenants.Add(tenant);
        db.Memberships.Add(new Membership { TenantId = tenant.Id, UserId = user.Id, Role = BusinessRole.ManagingDirector });
        db.Audit.Add(new AuditEntry { TenantId = tenant.Id, ActorId = user.Id, Action = "bootstrap", Kind = "membership", EntityId = user.Id, Revision = 1 });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        Console.WriteLine("Initial business created. Remove Bootstrap:Password from the environment after use.");
    }
}
