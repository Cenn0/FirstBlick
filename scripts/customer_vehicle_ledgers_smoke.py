"""Real API checks for retained customer conversations and vehicle operational records.

The first run creates isolated synthetic masters and records. After the application
restart every operation is read-only and compares the same identities and digests.
No message is sent, and no accounting or tax-compliance claim is made.
"""
import datetime
from decimal import Decimal
import hashlib
import json
import urllib.error
import urllib.parse
import uuid
from zoneinfo import ZoneInfo


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")).hexdigest()


def check_customer_vehicle_ledgers(call, tenant, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def post(path, body, revision=None, key=None):
        return call("POST", "/api/v1/" + path, body, tenant, revision=revision, key=key)

    def update(kind, current, **changes):
        return call("PUT", "/api/v1/" + kind + "/" + current["id"], {**current, **changes}, tenant, revision=current["revision"])

    def reject(method, path, body, revision=None, codes=()):
        try:
            call(method, "/api/v1/" + path, body, tenant, revision=revision)
        except urllib.error.HTTPError as error:
            assert 400 <= error.code < 500, "Expected a domain rejection, not a server failure"
            if codes:
                problem = json.loads(error.read())
                assert problem.get("title") in codes, problem.get("title")
            return
        raise AssertionError("An invalid ledger operation was accepted: " + path)

    if previous:
        assert previous["tenantId"] == tenant
        customer_id, contact_id, project_id = previous["customerId"], previous["contactId"], previous["projectId"]
        vehicle_id, driver_id, month = previous["vehicleId"], previous["driverId"], previous["month"]
        journal_originals, vehicle_originals = previous["journalOriginals"], previous["vehicleOriginals"]
    else:
        # Separate masters keep the existing contact/calendar smoke report stable:
        # each new journal command intentionally advances its customer's revision.
        customer = post("customers", {"name": "Container journal original customer"})
        customer_id = customer["id"]
        building = post("buildings", {"customerId": customer_id, "name": "Container ledger building", "address": "Synthetic ledger object"})
        project = post("projects", {"customerId": customer_id, "buildingId": building["id"], "name": "Container ledger original project", "status": "Active"})
        project_id = project["id"]
        contacts_path = "customers/" + customer_id + "/contacts"
        contact_details = {"name": "Container journal original contact", "function": "Synthetic coordinator", "email": "ledger@example.invalid",
            "phone": "", "notes": "Synthetic retained contact", "isPrimary": True}
        contacts = post(contacts_path, contact_details, customer["revision"])
        contact_id = contacts["contacts"][0]["id"]
        journal_path = "customers/" + customer_id + "/communications"
        occurred = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=2)).replace(microsecond=0)
        first_body = {"channel": "Telephone", "direction": "Incoming", "occurredAt": occurred.isoformat(), "subject": "Synthetic historical call",
            "text": "Manually recorded conversation before customer creation. No communication was sent.", "contactId": contact_id, "projectId": project_id}
        first_key = uuid.uuid4().hex
        first = post(journal_path, first_body, contacts["revision"], first_key)
        assert post(journal_path, first_body, contacts["revision"], first_key) == first, "Journal retry duplicated or changed its original"
        reject("POST", journal_path, {**first_body, "subject": "Rejected stale record"}, contacts["revision"])
        reject("POST", journal_path, {**first_body, "contactId": str(uuid.uuid4())}, first["customerRevision"], codes=("communication_contact",))
        second_body = {**first_body, "channel": "Note", "direction": "Internal", "subject": "Synthetic superseded note",
            "text": "An incorrect original that remains available after its explicit correction."}
        second = post(journal_path, second_body, first["customerRevision"])
        journal_originals = {entry["id"]: _digest(entry) for entry in (first["entry"], second["entry"])}
        void_path = journal_path + "/" + second["entry"]["id"] + "/void"
        void_body = {"sourceDigest": second["entry"]["digest"], "reason": "Synthetic note recorded in error; original retained"}
        void_key = uuid.uuid4().hex
        voided = post(void_path, void_body, second["customerRevision"], void_key)
        assert post(void_path, void_body, second["customerRevision"], void_key) == voided
        assert _digest(voided["entry"]) == journal_originals[second["entry"]["id"]]
        reject("POST", void_path, void_body, voided["customerRevision"], codes=("communication_already_void",))
        reject("PUT", "customer-communications/" + first["entry"]["id"], {**first["entry"], "text": "Rejected rewrite"}, 1)
        contacts = post(contacts_path + "/" + contact_id + "/update",
            {**contact_details, "name": "Container journal renamed contact"}, voided["customerRevision"])
        contacts = post(contacts_path + "/" + contact_id + "/archive", {}, contacts["revision"])
        update("customers", get("customers/" + customer_id), name="Container journal current customer")

        clock = get("vehicle-ledger/vehicles")
        driver = post("employees", {"name": "Container original ledger driver", "active": True})
        driver_id = driver["id"]
        vehicle = post("equipment", {"name": "Container original ledger vehicle", "inventoryNumber": "CONTAINER-LEDGER-VEHICLE",
            "kind": "Vehicle", "registration": "SYNTHETIC-LEDGER", "available": True})
        vehicle_id = vehicle["id"]
        # Both journeys belong to the same completed local day, including runs
        # around midnight/month boundaries and the tenant's DST transition.
        local_date = datetime.date.fromisoformat(clock["businessDate"]) - datetime.timedelta(days=1)
        start = datetime.datetime.combine(local_date, datetime.time(10), tzinfo=ZoneInfo(clock["timeZone"])).astimezone(datetime.timezone.utc)
        month = local_date.strftime("%Y-%m")
        trips_path = "equipment/" + vehicle_id + "/vehicle-trips"
        first_trip_body = {"driverEmployeeId": driver_id, "projectId": project_id, "start": start.isoformat(),
            "end": (start + datetime.timedelta(minutes=30)).isoformat(), "odometerStartKm": 1000, "odometerEndKm": 1025,
            "purpose": "Synthetic operational journey", "origin": "Synthetic depot", "destination": "Synthetic project", "notes": "Operational evidence only"}
        trip_key = uuid.uuid4().hex
        first_trip = post(trips_path, first_trip_body, vehicle["revision"], trip_key)
        assert post(trips_path, first_trip_body, vehicle["revision"], trip_key) == first_trip
        vehicle = first_trip["vehicle"]
        reject("POST", trips_path, {**first_trip_body, "purpose": "Rejected overlap"}, vehicle["revision"],
            codes=("vehicle_driver_overlap", "vehicle_trip_overlap"))
        second_trip_body = {**first_trip_body, "start": (start + datetime.timedelta(hours=1)).isoformat(),
            "end": (start + datetime.timedelta(hours=1, minutes=30)).isoformat(), "odometerStartKm": 1025, "odometerEndKm": 1065,
            "purpose": "Synthetic incorrect final odometer"}
        mistaken_trip = post(trips_path, second_trip_body, vehicle["revision"])
        vehicle = mistaken_trip["vehicle"]
        reverse_trip_path = trips_path + "/" + mistaken_trip["trip"]["entry"]["id"] + "/reverse"
        trip_reverse_body = {"sourceDigest": mistaken_trip["trip"]["entry"]["digest"], "reason": "Wrong end odometer; retain original"}
        reject("POST", reverse_trip_path, {**trip_reverse_body, "sourceDigest": "0" * 64}, vehicle["revision"], codes=("vehicle_source_mismatch",))
        trip_reverse_key = uuid.uuid4().hex
        reversed_trip = post(reverse_trip_path, trip_reverse_body, vehicle["revision"], trip_reverse_key)
        assert post(reverse_trip_path, trip_reverse_body, vehicle["revision"], trip_reverse_key) == reversed_trip
        vehicle = reversed_trip["vehicle"]
        reject("POST", reverse_trip_path, trip_reverse_body, vehicle["revision"], codes=("vehicle_already_reversed",))
        corrected_trip = post(trips_path, {**second_trip_body, "odometerEndKm": 1050, "purpose": "Synthetic corrected final odometer"}, vehicle["revision"])
        vehicle = corrected_trip["vehicle"]
        costs_path = "equipment/" + vehicle_id + "/vehicle-costs"
        cost_body = {"projectId": project_id, "spentOn": local_date.isoformat(), "category": "Fuel", "amount": 85,
            "currency": "EUR", "reference": "SYNTHETIC-FUEL-LEDGER", "notes": "Operational cost; no invoice posting"}
        cost_key = uuid.uuid4().hex
        cost = post(costs_path, cost_body, vehicle["revision"], cost_key)
        assert post(costs_path, cost_body, vehicle["revision"], cost_key) == cost
        vehicle = cost["vehicle"]
        mistaken_cost = post(costs_path, {**cost_body, "amount": 120, "category": "Repair", "reference": "SYNTHETIC-WRONG-REPAIR"}, vehicle["revision"])
        vehicle = mistaken_cost["vehicle"]
        reverse_cost_path = costs_path + "/" + mistaken_cost["cost"]["entry"]["id"] + "/reverse"
        reversed_cost = post(reverse_cost_path, {"sourceDigest": mistaken_cost["cost"]["entry"]["digest"], "reason": "Synthetic repair entered in error"}, vehicle["revision"])
        originals = [first_trip["trip"]["entry"], mistaken_trip["trip"]["entry"], corrected_trip["trip"]["entry"], cost["cost"]["entry"], mistaken_cost["cost"]["entry"]]
        vehicle_originals = {entry["id"]: _digest(entry) for entry in originals}
        assert _digest(reversed_trip["trip"]["entry"]) == vehicle_originals[mistaken_trip["trip"]["entry"]["id"]]
        assert _digest(reversed_cost["cost"]["entry"]) == vehicle_originals[mistaken_cost["cost"]["entry"]["id"]]
        reject("PUT", "vehicle-trips/" + first_trip["trip"]["entry"]["id"], {**first_trip["trip"]["entry"], "purpose": "Rejected rewrite"}, 1)
        update("equipment", get("equipment/" + vehicle_id), name="Container current ledger vehicle")
        update("employees", get("employees/" + driver_id), name="Container current ledger driver", active=False)
        update("projects", get("projects/" + project_id), name="Container current ledger project")

    customer = get("customers/" + customer_id)
    contacts = get("customers/" + customer_id + "/contacts")
    contact = next(entry for entry in contacts["contacts"] if entry["id"] == contact_id)
    assert contact["archived"] is True and contact["name"] == "Container journal renamed contact"
    project = get("projects/" + project_id)
    vehicle = get("equipment/" + vehicle_id)
    driver = get("employees/" + driver_id)
    assert driver["active"] is False
    journal = get("customers/" + customer_id + "/communications")
    assert journal["customerId"] == customer_id and journal["tenantId"] == tenant
    assert journal["customerRevision"] == customer["revision"] and journal["total"] == 2 and len(journal["items"]) == 2
    voids = {}
    for item in journal["items"]:
        entry = item["entry"]
        assert _digest(entry) == journal_originals[entry["id"]], "Original communication bytes changed"
        assert entry["customerName"] != customer["name"] and entry["contact"]["id"] == contact_id
        assert entry["contact"]["name"] != contact["name"] and entry["project"]["id"] == project_id
        assert entry["project"]["name"] != project["name"] and entry["revision"] == 1 and entry["archived"] is False
        if item["voiding"]:
            voiding = item["voiding"]
            assert voiding["sourceDigest"] == entry["digest"] and voiding["communicationId"] == entry["id"]
            assert voiding["customerId"] == customer_id and voiding["revision"] == 1
            voids[voiding["id"]] = _digest(voiding)
    assert len(voids) == 1
    ledger = get("equipment/" + vehicle_id + "/vehicle-ledger?" + urllib.parse.urlencode({"month": month}))
    assert ledger["vehicle"]["revision"] == vehicle["revision"] and ledger["vehicle"]["id"] == vehicle_id
    assert ledger["trips"]["total"] == 3 and len(ledger["trips"]["items"]) == 3
    assert ledger["costs"]["total"] == 2 and len(ledger["costs"]["items"]) == 2
    summary = ledger["summary"]
    assert summary["tripCount"] == 2 and summary["reversedTripCount"] == 1 and Decimal(str(summary["distanceKm"])) == Decimal("50")
    assert summary["costCount"] == 1 and summary["reversedCostCount"] == 1 and Decimal(str(summary["costAmount"])) == Decimal("85")
    assert Decimal(str(summary["costPerKm"])) == Decimal("1.7")
    reversals = {}
    for item in ledger["trips"]["items"] + ledger["costs"]["items"]:
        entry = item["entry"]
        assert _digest(entry) == vehicle_originals[entry["id"]], "Original vehicle bytes changed"
        assert entry["vehicleId"] == vehicle_id and entry["vehicleSnapshot"]["name"] != vehicle["name"]
        assert entry["projectId"] == project_id and entry["projectTitle"] != project["name"]
        assert entry["revision"] == 1 and entry["archived"] is False
        if "driverEmployeeId" in entry:
            assert entry["driverEmployeeId"] == driver_id and entry["driverName"] != driver["name"]
        if item["reversal"]:
            reversal = item["reversal"]
            assert reversal["sourceId"] == entry["id"] and reversal["sourceDigest"] == entry["digest"]
            assert reversal["vehicleId"] == vehicle_id and reversal["revision"] == 1
            reversals[reversal["id"]] = _digest(reversal)
    assert len(reversals) == 2
    evidence = {"tenantId": tenant, "customerId": customer_id, "contactId": contact_id, "projectId": project_id,
        "vehicleId": vehicle_id, "driverId": driver_id, "month": month, "journalOriginals": journal_originals,
        "vehicleOriginals": vehicle_originals, "communicationVoids": voids, "vehicleReversals": reversals,
        "customerSha256": _digest(customer), "contactsSha256": _digest(contacts), "projectSha256": _digest(project),
        "vehicleSha256": _digest(vehicle), "driverSha256": _digest(driver), "journalSha256": _digest(journal),
        "vehicleSummarySha256": _digest(summary)}
    if previous:
        for key, value in evidence.items():
            assert previous[key] == value, "Retained journal/vehicle evidence changed after restart: " + key
    return {**evidence, "status": "passed", "preservedAcrossRestart": bool(previous),
        "checks": ["actual customer journal record, idempotent replay, stale revision and foreign contact rejection",
            "separate journal void preserving the original content and digest",
            "historical customer, archived contact and renamed project snapshots retained",
            "actual vehicle trips, overlap rejection, immutable reversal and corrected odometer entry",
            "operational vehicle costs, reversal and effective 50 km / 85 EUR totals",
            "renamed vehicle, inactive driver and project identities retain original snapshots",
            "read-only identity, original, reversal and master-record comparison after restart"]}
