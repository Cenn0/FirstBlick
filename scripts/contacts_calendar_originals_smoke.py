"""Real API/S3 round trips for customer contacts, calendars and inspection originals.

The first run creates synthetic records and verifies idempotent writes. The
post-restart run only reads the retained identities and compares their digests.
No provider is contacted and no session credentials enter the evidence report.
"""
import base64
import hashlib
import json
import urllib.parse
import urllib.request
import uuid


ORIGINAL_PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
    validate=True,
)
CALENDAR_TITLE = "Container calendar: Dachwartung, Nord; Süd 🏠"


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")).hexdigest()


def check_contacts_calendar_originals(call, client, base, tenant, customer, equipment_inventory, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def post(path, body, revision=None, key=None):
        return call("POST", "/api/v1/" + path, body, tenant, revision=revision, key=key)

    def download(path, content_type, expected_sha=None):
        request = urllib.request.Request(base + "/api/v1/" + path,
            headers={"X-Tenant-Id": tenant, "Accept": content_type})
        with client.open(request, timeout=30) as response:
            data = response.read(65_537)
            assert 0 < len(data) <= 65_536, "Unexpected smoke download size"
            assert response.headers.get_content_type() == content_type
            assert response.headers.get("Content-Disposition", "").startswith("attachment;")
            actual_sha = hashlib.sha256(data).hexdigest()
            assert response.headers.get("X-Content-SHA256") == actual_sha
            if expected_sha is not None:
                assert actual_sha == expected_sha, "Retained download bytes changed"
            return data, actual_sha, {name.lower(): value for name, value in response.headers.items()}

    contacts_path = "customers/" + customer["id"] + "/contacts"
    inspection_id = equipment_inventory["inspectionIds"][1]
    inspection = get("equipment-inspections/" + inspection_id)
    assert inspection["equipmentId"] == equipment_inventory["equipmentId"]
    assert inspection["status"] == "Completed" and inspection["completion"]["result"] == "Passed"
    assert inspection["completion"]["digest"] == equipment_inventory["inspectionDigests"][1]
    inspection_snapshot_sha = _digest(inspection)
    original_sha = hashlib.sha256(ORIGINAL_PNG).hexdigest()
    documents_path = "equipment-inspections/" + inspection_id + "/documents"

    if previous:
        assert previous["customerId"] == customer["id"] and previous["tenantId"] == tenant
        assert previous["inspectionId"] == inspection_id
        assert previous["inspectionSnapshotSha256"] == inspection_snapshot_sha
        primary_id, archived_id = previous["primaryContactId"], previous["archivedContactId"]
        contract_id, visit_id = previous["contractId"], previous["visitId"]
        document_id = previous["documentId"]
    else:
        contacts = get(contacts_path)
        assert contacts["customerId"] == customer["id"] and contacts["tenantId"] == tenant
        assert contacts["contacts"] == [], "Smoke customer unexpectedly has existing structured contacts"
        primary_details = {"name": "Container primary contact", "function": "Synthetic site coordination",
            "email": "primary@example.test", "phone": "", "notes": "Synthetic contact, no message is sent", "isPrimary": False}
        first_key = uuid.uuid4().hex
        first = post(contacts_path, primary_details, contacts["revision"], first_key)
        replay = post(contacts_path, primary_details, contacts["revision"], first_key)
        assert replay == first and len(first["contacts"]) == 1, "Contact retry created a different record"
        primary_id = first["contacts"][0]["id"]
        second_details = {"name": "Container retained contact", "function": "Synthetic prior coordination",
            "email": "retained@example.test", "phone": "", "notes": "Archive retains this synthetic contact", "isPrimary": False}
        second = post(contacts_path, second_details, first["revision"])
        assert len(second["contacts"]) == 2
        archived_id = next(item["id"] for item in second["contacts"] if item["id"] != primary_id)
        selected = post(contacts_path + "/" + primary_id + "/update", {**primary_details, "isPrimary": True}, second["revision"])
        contacts = post(contacts_path + "/" + archived_id + "/archive", {}, selected["revision"])
        assert len(contacts["contacts"]) == 2

        assignment = get("schedule-assignments/" + equipment_inventory["assignmentId"])
        source_project = get("projects/" + assignment["projectId"])
        assert source_project["customerId"] == customer["id"]
        due_date = "2028-02-29"
        contract = post("maintenance-contracts", {"customerId": customer["id"], "buildingId": source_project["buildingId"],
            "title": CALENDAR_TITLE, "scope": "Private synthetic maintenance scope", "startDate": due_date,
            "nextDueDate": due_date, "intervalMonths": 12})
        contract_id = contract["id"]
        contract = post("maintenance-contracts/" + contract_id + "/configure", {"active": True,
            "automaticPlanning": False, "planningLeadDays": 14,
            "checklistTemplate": [{"title": "Private synthetic check", "instructions": "These details stay outside the calendar"}]}, contract["revision"])
        schedule_key = uuid.uuid4().hex
        visit = post("maintenance-contracts/" + contract_id + "/schedule", {"expectedDueDate": due_date}, contract["revision"], schedule_key)
        replay = post("maintenance-contracts/" + contract_id + "/schedule", {"expectedDueDate": due_date}, contract["revision"], schedule_key)
        assert replay == visit, "Repeated schedule command changed the retained occurrence"
        visit_id = visit["id"]

        metadata = {"name": "container-inspection-original.png", "contentType": "image/png",
            "size": len(ORIGINAL_PNG), "sha256": original_sha, "projectId": None}
        stage_key = uuid.uuid4().hex
        staged = post(documents_path, metadata, inspection["revision"], stage_key)
        assert staged["state"] == "Staged" and staged["versionId"] is None
        assert post(documents_path, metadata, inspection["revision"], stage_key) == staged
        document_id = staged["id"]
        # The same CookieJar handles login, CSRF rotation, raw upload and download.
        cookie_jar = next(handler.cookiejar for handler in client.handlers
                          if isinstance(handler, urllib.request.HTTPCookieProcessor))
        csrf = urllib.parse.unquote(next(cookie.value for cookie in cookie_jar if cookie.name == "XSRF-TOKEN"))
        upload_key = uuid.uuid4().hex

        def upload_original(replay_expected=False):
            request = urllib.request.Request(base + "/api/v1/" + documents_path + "/" + document_id + "/content",
                data=ORIGINAL_PNG, method="PUT", headers={"X-Tenant-Id": tenant, "Accept": "application/json",
                    "Content-Type": "image/png", "X-XSRF-TOKEN": csrf, "Idempotency-Key": upload_key,
                    "If-Match": '"' + str(staged["revision"]) + '"'})
            with client.open(request, timeout=30) as response:
                if replay_expected:
                    assert response.headers.get("Idempotency-Replayed") == "true"
                return json.loads(response.read(65_537))

        ready = upload_original()
        assert ready["id"] == document_id and ready["state"] == "Ready"
        assert ready["revision"] == staged["revision"] + 1
        assert upload_original(replay_expected=True) == ready, "Original retry wrote a different version"

    # Both invocations independently read the same public contracts. After a
    # restart this entire branch consists of GET requests only.
    contacts = get(contacts_path)
    assert contacts["customerId"] == customer["id"] and contacts["tenantId"] == tenant
    assert len(contacts["contacts"]) == 2 and {item["id"] for item in contacts["contacts"]} == {primary_id, archived_id}
    primary = next(item for item in contacts["contacts"] if item["id"] == primary_id)
    archived = next(item for item in contacts["contacts"] if item["id"] == archived_id)
    assert primary["isPrimary"] is True and primary["archived"] is False
    assert archived["isPrimary"] is False and archived["archived"] is True
    assert archived["archivedAt"] and archived["archivedBy"] and archived["name"] == "Container retained contact"
    retained_customer = get("customers/" + customer["id"])
    assert retained_customer["contacts"] == contacts["contacts"] and retained_customer["revision"] == contacts["revision"]
    assert retained_customer["contactName"] == customer.get("contactName", "")
    assert retained_customer["email"] == customer.get("email", "")

    contract = get("maintenance-contracts/" + contract_id)
    assert contract["automaticPlanning"] is False and contract["title"] == CALENDAR_TITLE
    visit = get("maintenance-visits/" + visit_id)
    assert visit["contractId"] == contract_id and visit["status"] == "Planned"
    assert visit["tenantId"] == tenant and visit["dueDate"] == "2028-02-29"
    project = get("projects/" + visit["projectId"])
    assert project["type"] == "Maintenance" and project["customerId"] == customer["id"]
    assert project["buildingId"] == contract["buildingId"]
    visits = get("maintenance-visits?" + urllib.parse.urlencode({"contractId": contract_id, "page": 1}))
    assert visits["total"] == 1 and len(visits["items"]) == 1 and visits["items"][0]["id"] == visit_id
    calendar_path = "maintenance-visits/" + visit_id + "/calendar?revision=" + str(visit["revision"])
    calendar_bytes, calendar_sha, calendar_headers = download(calendar_path, "text/calendar", previous.get("calendarSha256") if previous else None)
    assert calendar_headers.get("etag") == '"' + str(visit["revision"]) + '"'
    assert "no-store" in calendar_headers.get("cache-control", "")
    assert download(calendar_path, "text/calendar", calendar_sha)[0] == calendar_bytes
    calendar = calendar_bytes.decode("utf-8")
    assert calendar.endswith("\r\n") and "\n" not in calendar.replace("\r\n", "")
    assert "\r" not in calendar.replace("\r\n", "")
    assert all(len(line.encode("utf-8")) <= 75 for line in calendar.split("\r\n"))
    unfolded = calendar.replace("\r\n ", "").replace("\r\n\t", "")
    uid = "maintenance-" + tenant.replace("-", "") + "-" + visit_id.replace("-", "") + "@firstblick.invalid"
    assert "UID:" + uid + "\r\n" in unfolded
    assert "DTSTART;VALUE=DATE:20280229\r\n" in unfolded and "DTEND;VALUE=DATE:20280301\r\n" in unfolded
    assert "SEQUENCE:" + str(visit["revision"] - 1) + "\r\n" in unfolded
    assert "SUMMARY:Wartung fällig: Container calendar: Dachwartung\\, Nord\\; Süd 🏠\r\n" in unfolded
    assert "Private synthetic" not in unfolded and "METHOD:" not in unfolded and "ATTENDEE:" not in unfolded

    documents = get(documents_path)
    assert documents["inspectionId"] == inspection_id and documents["equipmentId"] == equipment_inventory["equipmentId"]
    assert documents["sourceDigest"] == inspection["completion"]["digest"]
    assert documents["total"] == 1 and len(documents["items"]) == 1, "Duplicate inspection originals appeared"
    document = documents["items"][0]
    assert document["id"] == document_id and document["state"] == "Ready" and document["tenantId"] == tenant
    assert document["sha256"] == original_sha and document["size"] == len(ORIGINAL_PNG)
    assert document["sourceKind"] == "equipment-inspections" and document["sourceId"] == inspection_id
    assert document["sourceDigest"] == inspection["completion"]["digest"] and document["projectId"] is None
    assert document["rendererVersion"] == "equipment-inspection-original-v1"
    assert document["versionId"] and document["versionId"] != "null", "A concrete S3 version is required"
    original_bytes, _, _ = download(documents_path + "/" + document_id + "/download", "image/png", original_sha)
    assert original_bytes == ORIGINAL_PNG
    assert _digest(get("equipment-inspections/" + inspection_id)) == inspection_snapshot_sha, "Attaching an original changed sealed inspection evidence"

    evidence = {"customerId": customer["id"], "tenantId": tenant,
        "primaryContactId": primary_id, "archivedContactId": archived_id, "contactsSnapshotSha256": _digest(contacts),
        "customerRevision": contacts["revision"], "contractId": contract_id, "contractSnapshotSha256": _digest(contract),
        "visitId": visit_id, "visitRevision": visit["revision"], "visitSnapshotSha256": _digest(visit),
        "maintenanceProjectId": project["id"], "calendarUid": uid, "calendarSha256": calendar_sha,
        "inspectionId": inspection_id, "inspectionSnapshotSha256": inspection_snapshot_sha,
        "documentId": document_id, "originalSha256": original_sha, "originalVersionId": document["versionId"],
        "originalSnapshotSha256": _digest(document)}
    if previous:
        for key, value in evidence.items():
            assert previous[key] == value, "Retained evidence changed after restart: " + key
    return {**evidence, "status": "passed", "preservedAcrossRestart": bool(previous),
        "checks": ["two retained customer contacts with explicit primary and archived identity",
            "idempotent contact, maintenance and original-file writes without duplicate records",
            "persisted maintenance occurrence and project with byte-exact all-day calendar",
            "calendar UID, revision, UTF-8 folding, escaping, hash and minimized content",
            "actual PNG upload through Identity/CSRF to a concrete S3 version",
            "inspection-bound exact-byte original download without rewriting completion evidence",
            "read-only identity, revision, source and digest comparison after application restart"]}
