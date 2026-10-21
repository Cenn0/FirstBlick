using FirstBlick.Core.Business;
using static FirstBlick.Core.Tests.Business.BusinessFixture;

namespace FirstBlick.Core.Tests.Business;

public sealed class EquipmentInspectionWorkflowTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static Equipment Equipment(bool available = true) => new()
    {
        TenantId = Tenant, Name = "Aufzug", InventoryNumber = "G-17", Kind = EquipmentKind.Machine,
        Available = available, NextInspectionOn = Today, CreatedAt = Now, UpdatedAt = Now
    };
    private static EquipmentInspection Plan(Equipment equipment, EquipmentInspectionKind kind = EquipmentInspectionKind.Inspection) =>
        EquipmentInspectionWorkflow.Plan(equipment, kind, Today, "Betrieblich festgelegte Prüfung", "Prüfumfang laut Geräteakte", Actor, Now);
    private static void Complete(Equipment equipment, EquipmentInspection inspection, EquipmentInspectionResult result = EquipmentInspectionResult.Passed, bool makeAvailable = false,
        DateOnly? performed = null, DateOnly? next = null, string evidence = "Prüfbericht 2026-17") =>
        EquipmentInspectionWorkflow.Complete(equipment, inspection, performed ?? Today, result, "Externe prüfende Person", evidence,
            result == EquipmentInspectionResult.Failed ? "Beschädigte Sicherung, außer Betrieb genommen" : "Umfang und Ergebnis dokumentiert",
            result == EquipmentInspectionResult.Passed ? next ?? Today.AddYears(1) : null,
            makeAvailable, Today, Actor, Now.AddHours(1));

    [Fact]
    public void Planning_retains_identity_scope_and_existing_due_date_without_claiming_a_result()
    {
        var equipment = Equipment(); var inspection = Plan(equipment);
        Assert.Equal(equipment.Id, inspection.EquipmentId); Assert.Equal(equipment.Id, inspection.EquipmentSnapshot!.Id);
        Assert.Equal("G-17", inspection.EquipmentSnapshot.InventoryNumber); Assert.Equal(64, inspection.ScopeDigest.Length);
        Assert.Equal(inspection.Id, equipment.PendingInspectionId); Assert.Equal(Today, equipment.NextInspectionOn);
        Assert.Null(equipment.LastInspectionId); Assert.Null(inspection.Completion); Assert.False(equipment.InspectionBlocked);
        Assert.True(equipment.Available); inspection.Validate(); equipment.Validate();
        var error = Assert.Throws<DomainException>(() => Plan(equipment)); Assert.Equal("inspection_already_planned", error.Code);
    }

    [Fact]
    public void Failure_blocks_dispatch_and_preserves_old_date_until_a_recorded_successful_repair()
    {
        var equipment = Equipment(); var inspection = Plan(equipment); Complete(equipment, inspection, EquipmentInspectionResult.Failed);
        Assert.False(equipment.Available); Assert.True(equipment.InspectionBlocked); Assert.Equal(Today, equipment.NextInspectionOn);
        Assert.Equal(inspection.Id, equipment.LastInspectionId); Assert.Equal(Today, equipment.LastInspectionOn); Assert.Null(equipment.PendingInspectionId);
        Assert.Equal("equipment_unavailable", Assert.Throws<DomainException>(() => EquipmentInspectionWorkflow.EnsureAssignable(equipment, Now, Now.AddHours(1), TimeZoneInfo.Utc)).Code);
        var repair = Plan(equipment, EquipmentInspectionKind.Repair); Complete(equipment, repair, makeAvailable: true);
        Assert.True(equipment.Available); Assert.False(equipment.InspectionBlocked); Assert.Equal(Today.AddYears(1), equipment.NextInspectionOn);
        Assert.Equal(repair.Id, equipment.LastInspectionId); Assert.Equal(EquipmentInspectionResult.Failed, inspection.Completion!.Result);
        inspection.Validate(); repair.Validate(); equipment.Validate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Positive_result_preserves_manual_availability_unless_release_is_explicit(bool available)
    {
        var equipment = Equipment(available); Complete(equipment, Plan(equipment));
        Assert.Equal(available, equipment.Available); Assert.False(equipment.InspectionBlocked);
    }

    [Fact]
    public void Completion_requires_evidence_and_cannot_invent_future_or_out_of_order_performance()
    {
        var equipment = Equipment(); var inspection = Plan(equipment);
        Assert.Equal("required", Assert.Throws<DomainException>(() => Complete(equipment, inspection, evidence: " ")).Code);
        Assert.Equal("inspection_future_result", Assert.Throws<DomainException>(() => Complete(equipment, inspection, performed: Today.AddDays(1))).Code);
        Assert.Equal(EquipmentInspectionStatus.Scheduled, inspection.Status); Assert.Null(equipment.LastInspectionId);
        Complete(equipment, inspection);
        var second = Plan(equipment);
        Assert.Equal("inspection_out_of_order", Assert.Throws<DomainException>(() => Complete(equipment, second, performed: Today.AddDays(-1))).Code);
        Assert.Equal(EquipmentInspectionStatus.Scheduled, second.Status);
    }

    [Fact]
    public void Success_requires_explicit_forward_date_and_failure_cannot_claim_release()
    {
        var equipment = Equipment(); var inspection = Plan(equipment);
        Assert.Equal("inspection_next_date", Assert.Throws<DomainException>(() => Complete(equipment, inspection, next: Today)).Code);
        Assert.Equal("inspection_failed_release", Assert.Throws<DomainException>(() => Complete(equipment, inspection, EquipmentInspectionResult.Failed, true)).Code);
        Assert.Null(inspection.Completion); Assert.True(equipment.Available); Assert.Null(equipment.LastInspectionId);
    }

    [Fact]
    public void Sealed_scope_and_result_reject_tampering_and_recompletion()
    {
        var equipment = Equipment(); var inspection = Plan(equipment); Complete(equipment, inspection);
        Assert.Equal("inspection_sealed", Assert.Throws<DomainException>(() => Complete(equipment, inspection)).Code);
        var original = inspection.Completion!;
        inspection.Completion = original with { EvidenceReference = "Anderer Beleg" };
        Assert.Equal("inspection_immutable", Assert.Throws<DomainException>(inspection.Validate).Code);
        inspection.Completion = original; inspection.Title = "Andere Prüfung";
        Assert.Equal("inspection_immutable", Assert.Throws<DomainException>(inspection.Validate).Code);
    }

    [Fact]
    public void Cancellation_retains_evidence_and_does_not_clear_a_previous_failure()
    {
        var equipment = Equipment(); Complete(equipment, Plan(equipment), EquipmentInspectionResult.Failed);
        var failedId = equipment.LastInspectionId; var inspection = Plan(equipment);
        EquipmentInspectionWorkflow.Cancel(equipment, inspection, "Termin wird verschoben", Actor, Now.AddHours(1));
        Assert.Equal(EquipmentInspectionStatus.Cancelled, inspection.Status); Assert.Equal("Termin wird verschoben", inspection.Cancellation!.Reason);
        Assert.Null(equipment.PendingInspectionId); Assert.Equal(failedId, equipment.LastInspectionId); Assert.True(equipment.InspectionBlocked);
        Assert.False(equipment.Available); Assert.Equal(Today, equipment.NextInspectionOn); inspection.Validate(); equipment.Validate();
        inspection.Cancellation = inspection.Cancellation with { Reason = "Verändert" };
        Assert.Equal("inspection_immutable", Assert.Throws<DomainException>(inspection.Validate).Code);
    }

    [Fact]
    public void Foreign_or_changed_equipment_cannot_receive_an_inspection_outcome()
    {
        var equipment = Equipment(); var inspection = Plan(equipment);
        equipment.TenantId = Guid.NewGuid();
        Assert.Equal("inspection_equipment_mismatch", Assert.Throws<DomainException>(() => Complete(equipment, inspection)).Code);
        equipment.TenantId = Tenant; equipment.InventoryNumber = "Anderes Gerät";
        Assert.Equal("inspection_equipment_changed", Assert.Throws<DomainException>(() => Complete(equipment, inspection)).Code);
    }

    [Fact]
    public void Dispatch_uses_business_local_exclusive_end_including_daylight_saving()
    {
        var equipment = Equipment(); equipment.NextInspectionOn = new(2026, 10, 25);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // On the DST fall-back day, local midnight at the end is 23:00 UTC.
        var end = new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero);
        EquipmentInspectionWorkflow.EnsureAssignable(equipment, end.AddHours(-25), end, zone);
        Assert.Equal("equipment_inspection_due", Assert.Throws<DomainException>(() =>
            EquipmentInspectionWorkflow.EnsureAssignable(equipment, end.AddHours(-1), end.AddTicks(1), zone)).Code);
    }

    [Theory]
    [InlineData(null, EquipmentInspectionDueStatus.Unscheduled)]
    [InlineData(-1, EquipmentInspectionDueStatus.Overdue)]
    [InlineData(0, EquipmentInspectionDueStatus.Due)]
    [InlineData(1, EquipmentInspectionDueStatus.Current)]
    public void Due_state_distinguishes_missing_due_today_overdue_and_future(int? offset, EquipmentInspectionDueStatus expected)
    {
        var equipment = Equipment(); equipment.NextInspectionOn = offset.HasValue ? Today.AddDays(offset.Value) : null;
        Assert.Equal(expected, EquipmentInspectionWorkflow.DueStatus(equipment, Today));
    }
}
