namespace FirstBlick.Infrastructure.Persistence;

/// <summary>Set-based open-item projection. Keep regression cases paired with PaymentWorkflow.Balance.</summary>
public static class FinanceReadModel
{
    // Values are exact PostgreSQL numeric values from the sealed aggregates. All three legs qualify TenantId.
    public const string OpenInvoiceIdsSql = """
        SELECT i."Id" AS "Value"
        FROM "Entities" i
        LEFT JOIN (
            SELECT "Data"->>'originalInvoiceId' AS invoice_id,
                   sum(("Data"->'snapshot'->>'gross')::numeric) AS credits
            FROM "Entities"
            WHERE "TenantId"=@tenant AND "Kind"='invoices' AND NOT "Archived"
              AND "Data"->>'status'='Issued' AND "Data"->>'kind'='CreditNote'
            GROUP BY "Data"->>'originalInvoiceId'
        ) c ON c.invoice_id=i."Id"::text
        LEFT JOIN (
            SELECT "Data"->>'invoiceId' AS invoice_id,
                   sum(CASE WHEN "Data"->>'direction'='Receipt' THEN ("Data"->>'amount')::numeric ELSE -("Data"->>'amount')::numeric END) AS paid,
                   sum(("Data"->>'discountAmount')::numeric) AS discount
            FROM "Entities"
            WHERE "TenantId"=@tenant AND "Kind"='payment-allocations' AND NOT "Archived"
              AND "Data"->>'reversedAt' IS NULL
            GROUP BY "Data"->>'invoiceId'
        ) p ON p.invoice_id=i."Id"::text
        WHERE i."TenantId"=@tenant AND i."Kind"='invoices' AND NOT i."Archived"
          AND i."Data"->>'status'='Issued' AND i."Data"->>'kind'<>'CreditNote'
          AND (i."Data"->'snapshot'->>'gross')::numeric - COALESCE(c.credits,0) - COALESCE(p.paid,0)
              - LEAST(COALESCE(p.discount,0),GREATEST(0,(i."Data"->'snapshot'->>'gross')::numeric-COALESCE(c.credits,0))) > 0
        """;
}
