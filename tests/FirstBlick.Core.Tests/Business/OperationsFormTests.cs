using FirstBlick.Core.Business;

namespace FirstBlick.Core.Tests.Business;

public sealed class OperationsFormTests
{
    [Fact]
    public void Monthly_maintenance_preserves_original_month_end_anchor()
    {
        var contract = new MaintenanceContract
        {
            TenantId = BusinessFixture.Tenant, CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "Roof inspection", Scope = "Visual inspection",
            StartDate = new(2027, 1, 31), NextDueDate = new(2027, 1, 31), IntervalMonths = 1
        };
        Assert.Equal(new DateOnly(2027, 2, 28), OperationsRules.CompleteMaintenance(contract, new(2027, 1, 31)));
        Assert.Equal(new DateOnly(2027, 3, 31), OperationsRules.CompleteMaintenance(contract, new(2027, 2, 28)));
        Assert.Equal("stale_occurrence", Assert.Throws<DomainException>(() => OperationsRules.CompleteMaintenance(contract, new(2027, 2, 28))).Code);
    }

    [Fact]
    public void Maintenance_stops_when_next_occurrence_exceeds_contract_end()
    {
        var contract = new MaintenanceContract
        {
            TenantId = BusinessFixture.Tenant, CustomerId = Guid.NewGuid(), BuildingId = Guid.NewGuid(), Title = "Inspection", Scope = "Inspection",
            StartDate = new(2026, 10, 20), NextDueDate = new(2026, 10, 20), EndDate = new(2026, 12, 31), IntervalMonths = 12
        };
        OperationsRules.CompleteMaintenance(contract, new(2026, 10, 20));
        Assert.False(contract.Active);
    }

    [Fact]
    public void Briefing_occurrence_deduplicates_the_repeated_dst_hour_by_local_date()
    {
        var preference = new NotificationPreference
        {
            TenantId = BusinessFixture.Tenant, UserId = BusinessFixture.Actor, Channels = [CommunicationChannel.Email],
            TimeZone = "Europe/Berlin", LocalTime = new(2, 15), Days = [DayOfWeek.Sunday]
        };
        var first = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var repeated = first.AddHours(1);
        Assert.True(OperationsRules.BriefingDue(preference, first)); Assert.True(OperationsRules.BriefingDue(preference, repeated));
        Assert.Equal(OperationsRules.BriefingOccurrenceKey(preference, first), OperationsRules.BriefingOccurrenceKey(preference, repeated));
    }

    [Fact]
    public void Invalid_timezone_does_not_silently_use_server_timezone()
    {
        var preference = new NotificationPreference { TenantId = BusinessFixture.Tenant, UserId = BusinessFixture.Actor, TimeZone = "Not/AZone" };
        Assert.Equal("unknown_timezone", Assert.Throws<DomainException>(preference.Validate).Code);
    }

    [Fact]
    public void Acceptance_requires_acknowledging_every_unaccepted_project_defect()
    {
        var project = Guid.NewGuid();
        var defect = new Defect { TenantId = BusinessFixture.Tenant, ProjectId = project, Title = "Tile", Description = "Cracked tile" };
        var acceptance = new Acceptance
        {
            TenantId = BusinessFixture.Tenant, ProjectId = project, Title = "Final inspection", OccurredAt = BusinessFixture.Now,
            CustomerRepresentative = "Customer", ContractorRepresentative = "Contractor"
        };
        Assert.Equal("unacknowledged_defects", Assert.Throws<DomainException>(() => OperationsRules.EnsureAcceptance(acceptance, [defect])).Code);
        acceptance.WithReservations = true; acceptance.Notes = "Replace cracked tile"; acceptance.DefectIds = [defect.Id];
        OperationsRules.EnsureAcceptance(acceptance, [defect]);
    }

    [Fact]
    public void Form_submission_freezes_the_published_schema_and_validates_field_types()
    {
        var definition = Definition(); FormRules.Publish(definition);
        var values = new Dictionary<string, string> { ["description"] = "Flashing inspected", ["area"] = "12.5", ["checked"] = "true", ["date"] = "2026-10-20", ["condition"] = "Good" };
        var submission = FormRules.Submit(definition, Guid.NewGuid(), BusinessFixture.Actor, values, BusinessFixture.Now);
        submission.Validate(); values["description"] = "Tampered original dictionary";
        Assert.Equal("Flashing inspected", submission.Values["description"]);
        definition.Fields[4].Choices[0] = "Changed";
        Assert.Contains("Good", submission.Fields[4].Choices);
        Assert.Equal("form_immutable", Assert.Throws<DomainException>(definition.Validate).Code);
        submission.Values["area"] = "13";
        Assert.Equal("submission_immutable", Assert.Throws<DomainException>(submission.Validate).Code);
    }

    [Theory]
    [InlineData("area", "NaN", "invalid_number")]
    [InlineData("area", "1,5", "invalid_number")]
    [InlineData("area", "101", "out_of_range")]
    [InlineData("date", "20.10.2026", "invalid_date")]
    [InlineData("date", "2026-02-30", "invalid_date")]
    [InlineData("checked", "yes", "invalid_boolean")]
    [InlineData("condition", "Invented", "invalid_choice")]
    public void Forms_reject_ambiguous_invalid_or_out_of_range_values(string key, string value, string code)
    {
        var definition = Definition(); FormRules.Publish(definition);
        var values = new Dictionary<string, string> { ["description"] = "Valid", [key] = value };
        Assert.Equal(code, Assert.Throws<DomainException>(() => FormRules.Submit(definition, Guid.NewGuid(), BusinessFixture.Actor, values, BusinessFixture.Now)).Code);
    }

    [Fact]
    public void Unknown_fields_and_self_training_review_are_rejected()
    {
        var definition = Definition(); FormRules.Publish(definition);
        Assert.Equal("unknown_field", Assert.Throws<DomainException>(() => FormRules.Submit(definition, Guid.NewGuid(), BusinessFixture.Actor, new() { ["description"] = "Text", ["hidden_role"] = "admin" }, BusinessFixture.Now)).Code);
        var employee = Guid.NewGuid();
        var record = new TrainingRecord { TenantId = BusinessFixture.Tenant, ApprenticeEmployeeId = employee, MentorEmployeeId = employee, WeekStart = new(2026, 10, 19) };
        Assert.Equal("self_review", Assert.Throws<DomainException>(record.Validate).Code);
    }

    [Fact]
    public void Business_reference_workflow_runs_renovation_from_customer_to_payment()
    {
        var tenant = BusinessFixture.Tenant;
        var customer = new Customer { TenantId = tenant, Name = "Example Customer", Address = "Example street" }; customer.Validate();
        var building = new Building { TenantId = tenant, CustomerId = customer.Id, Name = "House", Address = customer.Address }; building.Validate();
        var project = new Project { TenantId = tenant, CustomerId = customer.Id, BuildingId = building.Id, Name = "Roof renovation", Type = ProjectType.Renovation }; project.Validate();
        var quote = BusinessFixture.Quote(100, 10, 19); quote.CustomerId = customer.Id; quote.ProjectId = project.Id;
        CommercialWorkflow.IssueQuote(quote, "Q1"); CommercialWorkflow.AcceptQuote(quote, quote.Snapshot!.Digest, BusinessFixture.Now, new(2026, 10, 20));
        var order = CommercialWorkflow.CreateOrder(quote, "O1");
        var change = new ChangeOrder { TenantId = tenant, OrderId = order.Id, Title = "Additional flashing", Reason = "Found during works", Lines = [new() { Description = "Flashing", Quantity = 1, UnitMaterialCost = 50, VatPercent = 19 }] };
        CommercialWorkflow.AcceptChange(change, BusinessFixture.Now);
        var employee = new Employee { TenantId = tenant, Name = "Example Worker" }; employee.Validate();
        var diary = new DiaryEntry { TenantId = tenant, ProjectId = project.Id, EmployeeId = employee.Id, Date = new(2026, 10, 20), Work = "Roof and flashing installed" }; diary.Validate();
        var acceptance = new Acceptance { TenantId = tenant, ProjectId = project.Id, Title = "Roof complete", CustomerRepresentative = "Customer", ContractorRepresentative = employee.Name, OccurredAt = BusinessFixture.Now };
        OperationsRules.EnsureAcceptance(acceptance, []);
        var invoice = BusinessFixture.Draft(order); invoice.Lines.Add(change.Lines[0] with { });
        InvoiceWorkflow.IssueForOrder(invoice, order, [], [change], "I1", new(2026, 10, 20));
        Assert.Equal(1249.5m, invoice.Snapshot!.Gross);
        var allocation = PaymentWorkflow.Allocate(invoice, BusinessFixture.Bank(1249.5m), [], [], 1249.5m, 0, 0, "", BusinessFixture.Actor, BusinessFixture.Now);
        Assert.Equal(0m, PaymentWorkflow.Balance(invoice, [allocation], [], new(2026, 10, 20)).Outstanding);
        Assert.Equal(quote.Id, order.QuoteId); Assert.Equal(building.Id, project.BuildingId);
    }

    private static FormDefinition Definition() => new()
    {
        TenantId = BusinessFixture.Tenant, Name = "Roof inspection", Fields =
        [
            new() { Key = "description", Label = "Description", Required = true },
            new() { Key = "area", Label = "Area", Kind = FormFieldKind.Number, Minimum = 0, Maximum = 100 },
            new() { Key = "checked", Label = "Checked", Kind = FormFieldKind.YesNo },
            new() { Key = "date", Label = "Date", Kind = FormFieldKind.Date },
            new() { Key = "condition", Label = "Condition", Kind = FormFieldKind.Choice, Choices = ["Good", "Repair"] }
        ]
    };
}
