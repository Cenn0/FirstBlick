using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FirstBlick.Infrastructure.Persistence;

public sealed class TenantScope
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Core.Security.BusinessRole Role { get; set; }
}

public sealed class FirstBlickDbContext(DbContextOptions<FirstBlickDbContext> options, TenantScope scope)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public EntitySourceBudget SourceBudget { get; } = new();

    public DbSet<BusinessTenant> Tenants => Set<BusinessTenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<StoredEntity> Entities => Set<StoredEntity>();
    public DbSet<EntityLink> Links => Set<EntityLink>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<CommandReceipt> Receipts => Set<CommandReceipt>();
    public DbSet<OutboxJob> Jobs => Set<OutboxJob>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<DocumentObject> Documents => Set<DocumentObject>();
    public DbSet<NumberCounter> Counters => Set<NumberCounter>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ApplicationUser>().Property(x => x.DisplayName).HasMaxLength(160);
        b.Entity<BusinessTenant>().ToTable("Tenants");
        b.Entity<BusinessTenant>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<BusinessTenant>().Property(x => x.PolicyRevision).IsConcurrencyToken();
        b.Entity<Membership>().HasKey(x => new { x.TenantId, x.UserId });
        b.Entity<Membership>().Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
        b.Entity<Membership>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<Membership>().HasOne<BusinessTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Membership>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        var e = b.Entity<StoredEntity>();
        e.ToTable("Entities", t => { t.HasCheckConstraint("CK_Entity_Revision", "\"Revision\" > 0"); t.HasCheckConstraint("CK_Entity_Budget", "octet_length(\"Data\"::text) <= 2097152"); });
        e.HasKey(x => new { x.TenantId, x.Kind, x.Id });
        e.Property(x => x.Kind).HasMaxLength(80);
        e.Property(x => x.Data).HasColumnType("jsonb");
        e.Property(x => x.Revision).IsConcurrencyToken();
        e.Property(x => x.UniqueKey).HasMaxLength(512);
        e.HasIndex(x => new { x.TenantId, x.Kind, x.UniqueKey }).IsUnique().HasFilter("\"UniqueKey\" IS NOT NULL");
        e.HasIndex(x => new { x.TenantId, x.Kind, x.Archived, x.UpdatedAt });
        e.HasOne<BusinessTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        e.HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<EntityLink>().HasKey(x => new { x.TenantId, x.SourceKind, x.SourceId, x.TargetKind, x.TargetId });
        b.Entity<EntityLink>().HasOne<StoredEntity>().WithMany().HasForeignKey(x => new { x.TenantId, Kind = x.SourceKind, Id = x.SourceId }).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EntityLink>().HasOne<StoredEntity>().WithMany().HasForeignKey(x => new { x.TenantId, Kind = x.TargetKind, Id = x.TargetId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EntityLink>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<AuditEntry>().HasKey(x => x.Id);
        b.Entity<AuditEntry>().HasIndex(x => new { x.TenantId, x.At });
        b.Entity<AuditEntry>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<CommandReceipt>().HasKey(x => new { x.TenantId, x.ActorId, x.Key });
        b.Entity<CommandReceipt>().Property(x => x.Key).HasMaxLength(128);
        b.Entity<CommandReceipt>().Property(x => x.Response).HasColumnType("jsonb");
        b.Entity<CommandReceipt>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<OutboxJob>().Property(x => x.State).HasConversion<string>();
        b.Entity<OutboxJob>().Property(x => x.Payload).HasColumnType("jsonb");
        b.Entity<OutboxJob>().HasIndex(x => new { x.State, x.AvailableAt });
        b.Entity<OutboxJob>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<ApprovalRequest>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ApprovalRequest>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<Invitation>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<DocumentObject>().HasIndex(x => new { x.TenantId, x.ObjectKey }).IsUnique();
        b.Entity<DocumentObject>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<DocumentObject>().HasOne<BusinessTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DocumentObject>().HasOne<StoredEntity>().WithMany().HasForeignKey(x => new { x.TenantId, Kind = x.ProjectKind, Id = x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DocumentObject>().HasOne<StoredEntity>().WithMany().HasForeignKey(x => new { x.TenantId, Kind = x.SourceKind, Id = x.SourceId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DocumentObject>().ToTable("Documents", t =>
        {
            t.HasCheckConstraint("CK_Document_Size", "\"Size\" > 0 AND \"Size\" <= 26214400");
            t.HasCheckConstraint("CK_Document_Project", "(\"ProjectId\" IS NULL AND \"ProjectKind\" IS NULL) OR (\"ProjectId\" IS NOT NULL AND \"ProjectKind\" = 'projects')");
            t.HasCheckConstraint("CK_Document_Source", "(\"SourceId\" IS NULL AND \"SourceKind\" IS NULL) OR (\"SourceId\" IS NOT NULL AND \"SourceKind\" IS NOT NULL)");
        });
        b.Entity<DocumentObject>().HasQueryFilter(x => x.TenantId == scope.TenantId);
        b.Entity<NumberCounter>().HasKey(x => new { x.TenantId, x.Kind, x.Year });
        b.Entity<NumberCounter>().HasQueryFilter(x => x.TenantId == scope.TenantId);
    }
}
