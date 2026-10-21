using FirstBlick.Core.Business;
using FirstBlick.Core.Security;
using Xunit;

namespace FirstBlick.Core.Tests.Integrations;

public sealed class TradeSourceTests
{
    private static TradeSource Sample()
    {
        var s = new GaebSource { TenantId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), FileName = "Original.x83",
            Sha256 = new string('a', 64), Size = 300, ObjectVersionId = "synthetic-specific-version",
            ReceivedBy = Guid.NewGuid(), ReceivedAt = DateTimeOffset.UtcNow };
        s.Digest = s.ComputeDigest(); return s;
    }

    [Fact]
    public void Receipt_is_bound_to_business_document_version_and_exact_original_hash()
    {
        var s = Sample(); s.Validate(); var old = s.Digest;
        s.ObjectVersionId = "different-version";
        Assert.NotEqual(old, s.ComputeDigest());
        Assert.Equal("trade_source_changed", Assert.Throws<DomainException>(s.Validate).Code);
    }

    [Theory]
    [InlineData("../file.xml")]
    [InlineData("C:\\file.xml")]
    [InlineData("data/file.xml")]
    [InlineData("file.xml\n")]
    [InlineData(" file.xml")]
    [InlineData("file.html")]
    public void Unsafe_or_wrong_filenames_are_rejected(string name)
        => Assert.Throws<DomainException>(() => TradeSource.ValidateFileName(name, "gaeb"));

    [Fact]
    public void Invoice_archive_does_not_accept_GAEB_extension()
        => Assert.Throws<DomainException>(() => TradeSource.ValidateFileName("invoice.x83", "e-invoice"));

    [Fact]
    public void Unversioned_storage_and_changed_receipt_revision_are_rejected()
    {
        var s = Sample(); s.ObjectVersionId = "null"; s.Digest = s.ComputeDigest();
        Assert.Equal("storage_versioning_required", Assert.Throws<DomainException>(s.Validate).Code);
        s = Sample(); s.Revision = 2;
        Assert.Equal("trade_source_immutable", Assert.Throws<DomainException>(s.Validate).Code);
    }

    [Fact]
    public void Every_role_is_denied_generic_receipt_mutation_including_directors()
    {
        foreach (var role in Enum.GetValues<BusinessRole>())
        {
            Assert.False(AccessRules.CanWrite(role, "gaeb-sources"));
            Assert.False(AccessRules.CanWrite(role, "e-invoice-sources"));
        }
        Assert.True(AccessRules.CanRead(BusinessRole.Accounting, "e-invoice-sources"));
        Assert.False(AccessRules.CanRead(BusinessRole.SiteManager, "e-invoice-sources"));
        Assert.False(AccessRules.CanRead(BusinessRole.Worker, "gaeb-sources"));
    }

    [Fact]
    public void Tenant_or_source_changes_cannot_reuse_the_original_receipt_digest()
    {
        var s = Sample(); s.TenantId = Guid.NewGuid();
        Assert.Equal("trade_source_changed", Assert.Throws<DomainException>(s.Validate).Code);
        s = Sample(); s.Sha256 = new string('b', 64);
        Assert.Equal("trade_source_changed", Assert.Throws<DomainException>(s.Validate).Code);
    }
}
