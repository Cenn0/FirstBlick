# Receivable follow-up and disputes

Roadmap scope: **DP-08-04**, with current-permission and business-workflow evidence for **DP-03-07** and **DP-08-08**.

The `/collections` page (also available at `/collection-cases`) keeps a documented case for an issued receivable invoice. The invoice detail links to this workflow. The user selects a responsible person and review date, reviews the current financial sources, approves an internal text draft, and separately records an actual contact with its evidence. An approval does not send the draft.

## Retained financial context

Every balance and reminder preview uses the issued invoice, its issued corrections and its payment allocations, including reversals. It retains the frozen invoice parties and number. Updated customer master data cannot change the addressee of a historical invoice. The application distinguishes the outstanding balance, recorded retention and currently collectible amount. It uses the same `PaymentWorkflow.Balance` calculation as the invoice workflow; it does not add a second financial ledger.

Each source has its resource kind, ID, revision and SHA-256. Before producing a balance the API checks the stored tenant, resource kind, ID, revision and archived flag against the retained JSON body. The domain requires the source list to cover the calculated records exactly once. At most **1,000 source records in total** are accepted. Exceeding that budget returns an explicit error with no partial balance or reminder.

The preview binds the following data into a digest:

- Case ID, current revision and history digest.
- Explicit stage, response date, internal reason and additional text.
- Business-local date and the complete financial source snapshot.
- Resulting subject and text.

Approval recalculates these values under the tenant transaction lock. A payment, reversal, source revision, changed text, reassignment, case transition or change of business date makes the earlier preview unusable. The operator must read and approve a fresh preview. Monetary values cross HTTP as decimal strings, and the dedicated client formats them without converting them to floating-point numbers or recalculating the balance.

## Case actions

| Action | Required behavior |
| --- | --- |
| Open | Select an issued non-credit invoice with a recorded outstanding balance, a current finance member and explicit review date. Exactly one retained case exists for each invoice, including closed cases. |
| Preview | The case is active, no earlier draft is pending, and the recorded collectible balance is overdue. Choose the stage, response date and reason explicitly. |
| Approve internal draft | Match the case revision and the exact current preview digest. The full draft and source snapshot are retained. |
| Record actual contact | Identify the approved draft and its digest; record channel, UTC time and evidence. The contact cannot precede approval or be in the future. This is the user's documented account of an external contact, not provider delivery confirmation. |
| Withdraw draft | Withdraw an approved but uncontacted draft with a reason. The old draft stays in history. |
| Open dispute | Record a positive disputed amount no larger than the current outstanding balance, a reason, evidence and review date. Even a partial dispute pauses the entire case. An uncontacted draft is withdrawn and that transition is recorded. |
| Resolve dispute | Record an explicit explanation and evidence of resolution. This changes case handling only; it does not alter invoice amounts, corrections or payments. |
| Change review | Choose a current responsible finance member or new review date and explain the change. |
| Close as settled | Require both the current outstanding balance and customer credit to be zero. Pending drafts and unresolved disputes must be dealt with first. |
| Close for manual handling | Record why further handling occurs outside this workflow and where that decision is evidenced. The closure retains the actual financial balance and does not waive a claim. |
| Reopen | Reuse the same retained case, with a current outstanding balance, explicit new responsibility, review date, reason and evidence. The previous closure stays in history. |

Reminder stages are `PaymentReminder`, `FirstReminder` and `SecondReminder`. A subsequent stage requires a recorded contact for the preceding stage and expiry of that draft's explicitly selected response date. Recording a historical contact does not recalculate the current balance or rewrite the approved text. A contact recorded after its draft's response date may therefore permit the next stage immediately; the operator must select and review the next response date expressly. These are application workflow stages. The feature does not calculate statutory default, interest, legal fees or legal deadlines; it does not determine enforceability or replace a reviewed legal collection procedure.

## Authorization, transactions and history

Directors, office users and accounting users may read collection cases. Creating previews and making changes also require the enabled `finance` module and current online authority. Other roles cannot access either the detailed evidence or worklist. Disabling the module retains historical reading but rejects new actions. A responsible person must have active business membership, a confirmed available account and one of the supported finance roles.

All dedicated reads acquire the same tenant advisory transaction lock as business mutations, then re-read membership and permissions. Mutation, entity revision, links, application audit, generic `entity.changed` outbox event and idempotency receipt commit atomically. A receipt does not replace current authorization. Reusing a successful command's key and identical request returns the prior result; changing content with the same key is rejected. Generic creation, replacement and deletion of a case are rejected.

The case stores both a chained event history and a digest of its retained business state. Approved drafts and closures retain their complete financial snapshot. Opening a case or dispute retains the ledger digest in the event or dispute record; it does not retain another complete ledger snapshot. Contacts, disputes, closures and reopening explanations remain available. These application digests detect inconsistent modification; they are not an independently protected or externally timestamped archive against an administrator able to replace the database and recompute hashes.

The worklist queries invoice-labelled projections in PostgreSQL before counting or paging. It does not load each case's full evidence history. Search matches literal substrings in the frozen invoice number or buyer name. `%` and `_` do not become search wildcards. Filters have stable ordering and explicit limits; duplicated, unknown and malformed query fields are rejected. Reads return `Cache-Control: no-store, private`.

## HTTP contract

All routes below are under `/api/v1/collection-cases`, use the existing personal cookie and selected-tenant headers, and require the normal CSRF token for unsafe HTTP methods. Commands use `Idempotency-Key`; commands targeting an existing case also use its `If-Match` revision. Preview uses `If-Match` but writes no receipt.

| Method and relative route | Purpose |
| --- | --- |
| `GET /` or `/worklist` | Bounded projection with optional `page`, `pageSize`, `status`, `q`, `invoiceId`, each at most once. Defaults 1/25; maximum page size 100. |
| `GET /assignees` | Current eligible members, containing only user ID and display name, bounded to 1,000 people. |
| `GET /{id}` | Current retained case; ETag carries its revision. |
| `GET /{id}/balance` | Current complete source snapshot for that case. |
| `POST /from-invoice` | Open the unique case with invoice ID, owner, next review and reason. |
| `POST /{id}/step-preview` | Calculate an internal draft from explicit step input. |
| `POST /{id}/approve-step` | Approve step input and its exact preview digest. |
| `POST /{id}/withdraw-step` | Withdraw the named uncontacted step. |
| `POST /{id}/record-contact` | Record channel, occurrence time and evidence bound to the named step and digest. |
| `POST /{id}/dispute` | Pause for a documented disputed amount. |
| `POST /{id}/resolve-dispute` | Document resolution of the open dispute. |
| `POST /{id}/review` | Change responsibility or review date with a reason. |
| `POST /{id}/close` | Close as settled or for separately evidenced manual handling. |
| `POST /{id}/reopen` | Reopen the existing case with retained history. |

## Verification scope

`CollectionTests.cs` independently checks amount examples, source binding, changed previews, manual contact sequencing, retained-only balances, disputes, closure/reopening, invalid inputs and mutated evidence. `CollectionApiTests.cs` uses actual HTTP, identity cookies and a disposable PostgreSQL database. It checks idempotency, atomic retained state, role/tenant boundaries, membership revocation after an observed lock wait, worklist filtering and paging, frozen labels, invalid query inputs, rejected generic edits and fail-closed source validation/budgets.

The HTTP test's starting invoices are explicitly created historical fixtures in the isolated test database. The ordinary issue endpoint does not backdate or alter an existing issued invoice. Subsequent collection actions use real HTTP requests. `web/tests/browser/collections.spec.ts` separately contains API-intercepted client fixtures for the three configured viewports. Those client fixtures check the UI contract, uncertainty/retry and access-change behavior; they do not prove the server's financial arithmetic.

Real provider delivery, representative business cases, actual-device checks and a reviewed legal process remain separate scope. No outbound message provider is called by these collection commands.
