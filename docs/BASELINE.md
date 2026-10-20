# Source and requirement baseline

Recorded: 2026-10-20.

* Target: `Cenn0/FirstBlick`, public, default branch `main`. At the start of this implementation it contained `docs/ROADMAP.md` and no application code.
* Requirements: the 34-page project analysis dated 2026-10-19 and its 100-task roadmap, retaining DP-00 through DP-14 without reducing the required scope.
* Legacy comparison commit: `7b6060b9b3e99344eaed3eaa96a675e08c82adb3`; historical snapshot parent `688befe4c7a40fe6a60cf77e51a7372d32a87ca3`. This comparison does not prove equivalence to the last hosted application. Later missing features remain an inventory item.
* Current data: no authorized production database or object export was supplied. Record counts, actual document completeness, password migration and a live-data restore cannot be asserted.
* Reference inputs: five installer files are available for static examination. Three additional attachments did not arrive. Installer presence is not proof of a calculation algorithm, usage license, normative correctness or independent validation.
* New code: authored independently for FirstBlick. Legacy private source and third-party installer payloads are excluded from this repository.

## Required reference workflows

1. **Roof renovation:** customer, building and roof revision → measurement → priced quote → acceptance → order → change order → scheduling → time and material → acceptance/defects → invoice → partial and final payment. Exceptions include a changed quote revision, overlapping staff assignment, and a partial payment reversal.
2. **Repair:** reuse the permanent building/roof record, create a smaller project, assign field work, record time/photos and deficiencies, then invoice. Repeated visits retain one building history.
3. **Maintenance:** contract and next due date → recurring appointment → checklist/report → defect/follow-up → optional invoice and next scheduled visit. Personal notification rules determine whether an authorized configured channel is used.

These are synthetic engineering reference cases. Confirmation against an actual roofing business remains a separate roadmap condition.

## Definition of completed implementation

A task can be checked off only when its original result and acceptance conditions have evidence. The implementation report distinguishes implemented behavior, tests that actually ran, external inputs, and outstanding work. A successful compilation does not establish tax, building-physics or provider compatibility.

