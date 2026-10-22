"""Real API/S3 checks for reviewed template applications and retained ledger files.

The first invocation creates isolated synthetic business records. Subsequent
invocations only read and compare their identities, frozen evidence and bytes.
"""
import base64
import datetime
from decimal import Decimal
import hashlib
import json
import urllib.error
import urllib.parse
import urllib.request
import uuid


ORIGINAL_PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
    validate=True,
)


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")).hexdigest()


def check_project_templates_originals(call, client, base, tenant, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def post(path, body, revision=None, key=None):
        return call("POST", "/api/v1/" + path, body, tenant, revision=revision, key=key)

    def update(kind, current, **changes):
        return call("PUT", "/api/v1/" + kind + "/" + current["id"], {**current, **changes}, tenant, revision=current["revision"])

    def update_template(current, **changes):
        body = {key: current[key] for key in ("name", "description", "projectType", "tasks")}
        return call("PUT", "/api/v1/project-templates/" + current["id"], {**body, **changes}, tenant, revision=current["revision"])

    def reject(method, path, body, revision=None):
        try:
            call(method, "/api/v1/" + path, body, tenant, revision=revision)
        except urllib.error.HTTPError as error:
            assert 400 <= error.code < 500, "Expected a domain rejection, not a server error"
            return
        raise AssertionError("An invalid operation was accepted: " + path)

    def upload(path, document, key, replay=False):
        cookie_jar = next(handler.cookiejar for handler in client.handlers
                          if isinstance(handler, urllib.request.HTTPCookieProcessor))
        csrf = urllib.parse.unquote(next(cookie.value for cookie in cookie_jar if cookie.name == "XSRF-TOKEN"))
        request = urllib.request.Request(base + "/api/v1/" + path + "/" + document["id"] + "/content",
            data=ORIGINAL_PNG, method="PUT", headers={"X-Tenant-Id": tenant, "Accept": "application/json",
                "Content-Type": "image/png", "X-XSRF-TOKEN": csrf, "Idempotency-Key": key,
                "If-Match": '"' + str(document["revision"]) + '"'})
        with client.open(request, timeout=30) as response:
            if replay:
                assert response.headers.get("Idempotency-Replayed") == "true"
            return json.loads(response.read(65_537))

    def download(path):
        request = urllib.request.Request(base + "/api/v1/" + path,
            headers={"X-Tenant-Id": tenant, "Accept": "image/png"})
        with client.open(request, timeout=30) as response:
            data = response.read(65_537)
            assert data == ORIGINAL_PNG, "Retained original bytes changed"
            assert response.headers.get_content_type() == "image/png"
            assert response.headers.get("Content-Disposition", "").startswith("attachment;")
            assert response.headers.get("X-Content-SHA256") == hashlib.sha256(data).hexdigest()
            assert "no-store" in response.headers.get("Cache-Control", "")

    if previous:
        assert previous["tenantId"] == tenant
        project_id, template_id = previous["projectId"], previous["templateId"]
        application_id, task_ids = previous["applicationId"], previous["taskIds"]
        customer_id, communication_id = previous["customerId"], previous["communicationId"]
        vehicle_id, cost_id, month = previous["vehicleId"], previous["costId"], previous["month"]
        document_ids = previous["documentIds"]
    else:
        customer = post("customers", {"name": "Container template and original customer"})
        customer_id = customer["id"]
        building = post("buildings", {"customerId": customer_id, "name": "Container template object", "address": "Synthetic site"})
        project = post("projects", {"customerId": customer_id, "buildingId": building["id"], "name": "Container reviewed template project",
            "type": "Renovation", "status": "Planning", "plannedStart": "2028-02-28", "plannedEnd": "2028-03-31"})
        project_id = project["id"]
        template = post("project-templates", {"name": "Container reviewed renovation", "description": "Synthetic calendar-day template",
            "projectType": "Renovation", "tasks": [
                {"title": "Prepare site", "description": "Two calendar days before start", "offsetDays": -2},
                {"title": "Open site", "description": "On the planned start", "offsetDays": 0},
                {"title": "Check roof", "description": "Explicit leap-day check", "offsetDays": 1},
                {"title": "Review work", "description": "Three calendar days after start", "offsetDays": 3}]})
        template_id = template["id"]
        request = {"templateId": template_id, "templateRevision": template["revision"], "anchorDate": None}
        old_preview = post("projects/" + project_id + "/template-preview", request, project["revision"])
        template = update_template(template, description="Explicitly revised synthetic template before confirmation")
        reject("POST", "projects/" + project_id + "/template-apply", {**request, "previewDigest": old_preview["digest"]}, project["revision"])
        request["templateRevision"] = template["revision"]
        preview = post("projects/" + project_id + "/template-preview", request, project["revision"])
        assert preview["anchorDate"] == "2028-02-28" and preview["anchorKind"] == "ProjectStart"
        assert [task["dueDate"] for task in preview["tasks"]] == ["2028-02-26", "2028-02-28", "2028-02-29", "2028-03-02"]
        reject("POST", "projects/" + project_id + "/template-apply", {**request, "previewDigest": "0" * 64}, project["revision"])
        body = {**request, "previewDigest": preview["digest"]}
        apply_key = uuid.uuid4().hex
        result = post("projects/" + project_id + "/template-apply", body, project["revision"], apply_key)
        assert post("projects/" + project_id + "/template-apply", body, project["revision"], apply_key) == result
        application_id = result["application"]["id"]
        task_ids = [task["id"] for task in result["tasks"]]
        assert len(task_ids) == 4 and len(set(task_ids)) == 4
        reject("POST", "projects/" + project_id + "/template-apply", body, result["projectRevision"])
        assert result["application"]["preview"] == preview
        for task in result["tasks"]:
            assert task["projectId"] == project_id and task["assignedEmployeeId"] is None and task["status"] == "Open"
            assert task["templateOrigin"]["applicationId"] == application_id
            assert task["templateOrigin"]["applicationDigest"] == result["application"]["digest"]
        # Current task contents and future template revisions are independent of
        # the retained application; its original facts must not be reconstructed.
        first_task = result["tasks"][0]
        update("tasks", first_task, title="Completed preparation after reviewed adoption", status="Done")
        template = update_template(template, name="Container template renamed after adoption")
        post("project-templates/" + template_id + "/archive", {"archived": True}, template["revision"])

        occurred_at = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=1)).replace(microsecond=0)
        communication_result = post("customers/" + customer_id + "/communications", {
            "channel": "Note", "direction": "Internal", "occurredAt": occurred_at.isoformat(),
            "subject": "Synthetic communication with an original", "text": "Retain the exact attachment after a correction.",
            "contactId": None, "projectId": project_id}, customer["revision"])
        communication = communication_result["entry"]
        communication_id = communication["id"]
        clock = get("vehicle-ledger/vehicles")
        spent_on = clock["businessDate"]
        month = spent_on[:7]
        vehicle = post("equipment", {"name": "Container original-cost vehicle", "inventoryNumber": "CONTAINER-ORIGINAL-COST",
            "kind": "Vehicle", "registration": "SYNTHETIC-ORIGINAL", "available": True})
        vehicle_id = vehicle["id"]
        cost_result = post("equipment/" + vehicle_id + "/vehicle-costs", {"projectId": project_id,
            "spentOn": spent_on, "category": "Fuel", "amount": 37.5, "currency": "EUR",
            "reference": "SYNTHETIC-ORIGINAL-37", "notes": "Operational cost with a separate original; no invoice posting"}, vehicle["revision"])
        cost = cost_result["cost"]["entry"]
        cost_id = cost["id"]
        metadata = {"name": "synthetic-ledger-original.png", "contentType": "image/png", "size": len(ORIGINAL_PNG),
            "sha256": hashlib.sha256(ORIGINAL_PNG).hexdigest(), "projectId": None}
        document_ids = {}
        for kind, entry in (("customer-communications", communication), ("vehicle-costs", cost)):
            path = kind + "/" + entry["id"] + "/documents"
            stage_key = uuid.uuid4().hex
            staged = post(path, metadata, entry["revision"], stage_key)
            assert post(path, metadata, entry["revision"], stage_key) == staged
            assert staged["state"] == "Staged" and staged["sourceDigest"] == entry["digest"]
            reject("PUT", "documents/" + staged["id"] + "/content", {}, staged["revision"])
            upload_key = uuid.uuid4().hex
            ready = upload(path, staged, upload_key)
            assert upload(path, staged, upload_key, replay=True) == ready
            assert ready["state"] == "Ready" and ready["versionId"] and ready["versionId"] != "null"
            # A second independent file is retained as well. Archive affects its
            # listing state, while both source-specific and generic reads survive.
            second = post(path, {**metadata, "name": "synthetic-ledger-supplement.png"}, entry["revision"])
            second_ready = upload(path, second, uuid.uuid4().hex)
            post("documents/" + second_ready["id"] + "/archive", {}, second_ready["revision"])
            document_ids[kind] = [ready["id"], second_ready["id"]]

        post("customers/" + customer_id + "/communications/" + communication_id + "/void",
            {"sourceDigest": communication["digest"], "reason": "Synthetic correction; original files retained"}, communication_result["customerRevision"])
        post("equipment/" + vehicle_id + "/vehicle-costs/" + cost_id + "/reverse",
            {"sourceDigest": cost["digest"], "reason": "Synthetic reversal; original files retained"}, cost_result["vehicle"]["revision"])
        for kind, entry in (("customer-communications", communication), ("vehicle-costs", cost)):
            reject("POST", kind + "/" + entry["id"] + "/documents", metadata, entry["revision"])

    application = get("project-template-applications/" + application_id)
    template = get("project-templates/" + template_id)
    project = get("projects/" + project_id)
    assert template["archived"] is True and template["name"] != application["preview"]["template"]["name"]
    history = get("projects/" + project_id + "/template-applications")
    assert history["total"] == 1 and history["items"][0]["id"] == application_id
    tasks = [get("tasks/" + task_id) for task_id in task_ids]
    assert tasks[0]["status"] == "Done" and tasks[0]["title"] != application["preview"]["tasks"][0]["title"]
    assert get("tasks?projectId=" + project_id)["total"] == 4, "Template retries created duplicate tasks"
    journal = get("customers/" + customer_id + "/communications")
    assert journal["total"] == 1 and journal["items"][0]["entry"]["id"] == communication_id
    assert journal["items"][0]["voiding"]["sourceDigest"] == journal["items"][0]["entry"]["digest"]
    ledger = get("equipment/" + vehicle_id + "/vehicle-ledger?month=" + month)
    assert ledger["costs"]["total"] == 1 and ledger["summary"]["costCount"] == 0
    assert ledger["summary"]["reversedCostCount"] == 1 and Decimal(str(ledger["summary"]["costAmount"])) == Decimal("0")
    documents = {}
    for kind, source_id in (("customer-communications", communication_id), ("vehicle-costs", cost_id)):
        path = kind + "/" + source_id + "/documents"
        listing = get(path)
        assert listing["total"] == 2 and listing["canAttach"] is False
        assert {document["id"] for document in listing["items"]} == set(document_ids[kind])
        assert {document["state"] for document in listing["items"]} == {"Ready", "Archived"}
        for document in listing["items"]:
            assert document["sourceKind"] == kind and document["sourceId"] == source_id and document["projectId"] is None
            assert document["sourceDigest"] == listing["sourceDigest"]
            assert document["versionId"] and document["versionId"] != "null"
            download(path + "/" + document["id"] + "/download")
            download("documents/" + document["id"] + "/download")
            documents[document["id"]] = _digest(document)

    evidence = {"tenantId": tenant, "projectId": project_id, "templateId": template_id, "applicationId": application_id,
        "taskIds": task_ids, "customerId": customer_id, "communicationId": communication_id, "vehicleId": vehicle_id,
        "costId": cost_id, "month": month, "documentIds": document_ids, "originalSha256": hashlib.sha256(ORIGINAL_PNG).hexdigest(),
        "templateSha256": _digest(template), "applicationSha256": _digest(application), "projectSha256": _digest(project),
        "taskSha256": {task["id"]: _digest(task) for task in tasks}, "journalSha256": _digest(journal),
        "ledgerSha256": _digest({key: ledger[key] for key in ("vehicle", "trips", "costs", "summary")}), "documentSha256": documents}
    if previous:
        for key, value in evidence.items():
            assert previous[key] == value, "Retained template/original evidence changed after restart: " + key
    return {**evidence, "status": "passed", "preservedAcrossRestart": bool(previous), "checks": [
        "reviewed template preview with negative offsets, leap day and stale/digest rejection",
        "atomic task adoption, retained source snapshots and duplicate/idempotency protection",
        "edited task and archived revised template preserve the original application",
        "multiple versioned originals at customer communications and vehicle costs",
        "exact original bytes through source and generic download after source correction and document archive",
        "source correction blocks further attachment without changing effective ledger totals",
        "read-only identity, snapshot, source digest, version and byte comparison after restart"]}
