"""Real HTTP checks for inspection history and explicitly sourced warehouse chains.

The second call after the Compose restart only reads these records. All identities
and evidence references below are synthetic; no supplier or inspector is contacted.
"""
import datetime
from decimal import Decimal
import json
import urllib.error
import urllib.parse
import uuid


def check_equipment_and_inventory(call, tenant, customer, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def post(path, body, revision=None, key=None):
        return call("POST", "/api/v1/" + path, body, tenant, revision=revision, key=key)

    def reject(method, path, body, revision=None, codes=()):
        try:
            call(method, "/api/v1/" + path, body, tenant, revision=revision)
        except urllib.error.HTTPError as error:
            assert 400 <= error.code < 500, "Expected a domain rejection, not a server failure"
            problem = json.loads(error.read())
            if codes:
                assert problem.get("title") in codes, problem.get("title")
            return
        raise AssertionError("An invalid transition was accepted: " + path)

    def stock(material_id, location):
        return get("materials/" + material_id + "/stock?" + urllib.parse.urlencode({"location": location}))

    if previous:
        equipment_id = previous["equipmentId"]
        material_id = previous["materialId"]
        purchase_id = previous["purchaseId"]
        receipt_id = previous["receiptId"]
        first_id, second_id = previous["transferIds"]
        return_id = previous["returnId"]
        assignment_id = previous["assignmentId"]
        inspection_ids = previous["inspectionIds"]
        pending_id = previous["pendingInspectionId"]
        evidence_digests = previous["inspectionDigests"]
    else:
        clock = get("equipment/inspection-worklist")
        today = datetime.date.fromisoformat(clock["businessDate"])
        followup = (today + datetime.timedelta(days=180)).isoformat()
        equipment = post("equipment", {"name": "Container inspection machine", "inventoryNumber": "CONTAINER-CHECK-1",
            "kind": "Machine", "location": "Werkstatt", "nextInspectionOn": followup, "available": True})
        equipment_id = equipment["id"]
        building = post("buildings", {"name": "Container dispatch building", "address": "Synthetic object address", "customerId": customer["id"]})
        project = post("projects", {"name": "Container inspection dispatch", "customerId": customer["id"], "buildingId": building["id"], "status": "Active"})
        employee = post("employees", {"name": "Container synthetic employee", "active": True})
        start = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)).replace(microsecond=0)
        assignment_body = {"title": "Existing assignment retains failed equipment evidence", "employeeId": employee["id"],
            "projectId": project["id"], "start": start.isoformat(), "end": (start + datetime.timedelta(hours=1)).isoformat(),
            "equipmentIds": [equipment_id]}
        assignment = post("schedule-assignments", assignment_body)
        assignment_id = assignment["id"]
        planned = post("equipment/" + equipment_id + "/inspection-plan", {"kind": "Inspection", "scheduledOn": today.isoformat(),
            "title": "Synthetic inspection", "notes": "Explicit container check"}, equipment["revision"])
        failed = post("equipment/" + equipment_id + "/inspection-complete", {
            "inspectionId": planned["inspection"]["id"], "inspectionRevision": planned["inspection"]["revision"],
            "performedOn": today.isoformat(), "result": "Failed", "inspector": "Synthetic inspector",
            "evidenceReference": "CONTAINER-FAILED-CHECK", "notes": "Synthetic documented defect", "nextInspectionOn": None,
            "makeAvailable": False}, planned["equipment"]["revision"])
        assert failed["equipment"]["inspectionBlocked"] is True and failed["equipment"]["available"] is False
        assert failed["affectedAssignmentCount"] == 1
        rejected_assignment = {**assignment_body, "start": (start + datetime.timedelta(hours=2)).isoformat(),
            "end": (start + datetime.timedelta(hours=3)).isoformat()}
        reject("POST", "schedule-assignments", rejected_assignment, codes=("equipment_unavailable",))
        original_equipment = get("equipment/" + equipment_id)
        reject("PUT", "equipment/" + equipment_id, {**original_equipment, "available": True}, original_equipment["revision"])
        repair = post("equipment/" + equipment_id + "/inspection-plan", {"kind": "Repair", "scheduledOn": today.isoformat(),
            "title": "Synthetic repair and reassessment", "notes": "Deployment assessment after repair"}, failed["equipment"]["revision"])
        passed = post("equipment/" + equipment_id + "/inspection-complete", {
            "inspectionId": repair["inspection"]["id"], "inspectionRevision": repair["inspection"]["revision"],
            "performedOn": today.isoformat(), "result": "Passed", "inspector": "Synthetic inspector",
            "evidenceReference": "CONTAINER-PASSED-REASSESSMENT", "notes": "Documented deployment assessment",
            "nextInspectionOn": followup, "makeAvailable": True}, repair["equipment"]["revision"])
        assert passed["equipment"]["inspectionBlocked"] is False and passed["equipment"]["available"] is True
        assert passed["affectedAssignmentCount"] == 0
        planned_followup = post("equipment/" + equipment_id + "/inspection-plan", {"kind": "Inspection", "scheduledOn": followup,
            "title": "Explicit next check", "notes": "Operator-chosen follow-up date"}, passed["equipment"]["revision"])
        pending_id = planned_followup["inspection"]["id"]
        inspection_ids = [failed["inspection"]["id"], passed["inspection"]["id"]]
        evidence_digests = [failed["inspection"]["completion"]["digest"], passed["inspection"]["completion"]["digest"]]

        supplier = post("suppliers", {"name": "Container synthetic supplier"})
        material = post("materials", {"name": "Container source-bound material", "sku": "CONTAINER-CHAIN-1", "unit": "Stk"})
        material_id = material["id"]
        line_id = str(uuid.uuid4())
        purchase = post("purchase-orders", {"supplierId": supplier["id"], "title": "Container warehouse chain purchase",
            "lines": [{"id": line_id, "materialId": material_id, "description": "Synthetic source-linked material", "quantity": "20", "unitNet": "10", "vatPercent": "19"}]})
        purchase_id = purchase["id"]
        purchase = post("purchase-orders/" + purchase_id + "/issue", {}, purchase["revision"])
        received = post("purchase-orders/" + purchase_id + "/receive", {"lineId": line_id, "quantity": "20", "location": "Hauptlager"}, purchase["revision"])
        receipt_id = received["receipt"]["id"]
        first = post("materials/" + material_id + "/transfer", {"fromLocation": "Hauptlager", "toLocation": "Außenlager",
            "quantity": "12", "unitCost": "10", "expectedFromOnHand": "20", "reason": "Synthetic first source-bound transfer", "transferSourceId": receipt_id})
        first_id = first["incoming"]["id"]
        second = post("materials/" + material_id + "/transfer", {"fromLocation": "Außenlager", "toLocation": "Baustellenlager",
            "quantity": "8", "unitCost": "10", "expectedFromOnHand": "12", "reason": "Synthetic second source-bound transfer", "transferSourceId": first_id})
        second_id = second["incoming"]["id"]
        returned = post("stock-movements/" + receipt_id + "/supplier-return", {"quantity": "3", "reason": "Synthetic return via two warehouse hops",
            "returnTransferId": second_id}, received["receipt"]["revision"])
        return_id = returned["movement"]["id"]
        # Additional stock from a separate inventory-count record must not increase this chain's allowance.
        post("materials/" + material_id + "/inventory-count", {"location": "Baustellenlager", "expectedOnHand": "5", "countedOnHand": "10",
            "unitCost": "10", "reason": "Synthetic extra stock with separate evidence", "evidenceReference": "CONTAINER-EXTRA-COUNT"})
        reject("POST", "stock-movements/" + receipt_id + "/supplier-return", {"quantity": "6", "reason": "Reject chain over-allocation despite free stock",
            "returnTransferId": second_id}, received["receipt"]["revision"], ("supplier_return_transfer_exceeded",))

    history = get("equipment/" + equipment_id + "/inspection-history")
    assert history["equipment"]["tenantId"] == tenant
    assert history["equipment"]["available"] is True and history["equipment"]["inspectionBlocked"] is False
    assert history["equipment"]["lastInspectionId"] == inspection_ids[1] and history["equipment"]["pendingInspectionId"] == pending_id
    assert history["pendingInspection"]["id"] == pending_id and history["pendingInspection"]["status"] == "Scheduled"
    for inspection_id, digest, outcome in zip(inspection_ids, evidence_digests, ("Failed", "Passed")):
        inspection = get("equipment-inspections/" + inspection_id)
        assert inspection["equipmentId"] == equipment_id and inspection["status"] == "Completed"
        assert inspection["completion"]["digest"] == digest and inspection["completion"]["result"] == outcome
    assert get("schedule-assignments/" + assignment_id)["equipmentIds"] == [equipment_id]
    trace = get("stock-movements/" + second_id + "/transfer-chain")
    assert trace["originalReceiptId"] == receipt_id and trace["sourceMovementId"] == second_id
    assert [hop["incomingId"] for hop in trace["transfers"]] == [first_id, second_id]
    assert Decimal(trace["evidenceRemaining"]) == 5
    returned = get("stock-movements/" + return_id)
    assert returned["returnTransferId"] == second_id and returned["reference"] == receipt_id and Decimal(returned["quantity"]) == -3
    assert get("purchase-orders/" + purchase_id)["status"] == "PartiallyReceived"
    balances = {}
    for location, expected in (("Hauptlager", 8), ("Außenlager", 4), ("Baustellenlager", 10)):
        current = stock(material_id, location)
        assert Decimal(current["onHand"]) == expected and Decimal(current["available"]) == expected
        balances[location] = current["onHand"]
    return {"status": "passed", "preservedAcrossRestart": bool(previous), "equipmentId": equipment_id,
        "inspectionIds": inspection_ids, "inspectionDigests": evidence_digests, "pendingInspectionId": pending_id,
        "assignmentId": assignment_id, "materialId": material_id, "purchaseId": purchase_id, "receiptId": receipt_id,
        "transferIds": [first_id, second_id], "returnId": return_id, "balances": balances,
        "checks": ["failed inspection retained despite an existing assignment", "new dispatch and manual bypass refused",
            "documented repair and explicit release", "original failed/passed evidence and next appointment retained",
            "two-hop receipt provenance", "unrelated stock cannot increase source allowance", "supplier return and purchase fulfillment"]}
