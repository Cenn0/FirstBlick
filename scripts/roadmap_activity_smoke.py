"""Real API checks for contact history, dependencies, workdays and report exports.

The first run creates isolated synthetic records. Later runs only read the same
records and compare retained evidence; report capture times may advance.
"""
import csv
import datetime
import hashlib
import io
import json
import urllib.error
import urllib.parse
import urllib.request
import uuid
from zoneinfo import ZoneInfo


CSV_HEADER = ("Bereich;Kennung;Revision;Projekt;Bezeichnung;Status aktuell;Archiviert;Angelegt am;"
              "Faellig am aktuell;Zeitraum von;Zeitraum bis;Vorzeitraum von;Vorzeitraum bis;Zeitzone;"
              "Betrieb;Berichtspruefsumme").split(";")


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def _snapshot(report):
    return {key: value for key, value in report.items() if key != "capturedAt"}


def _csv_value(value):
    trimmed = value.lstrip()
    return "'" + value if (trimmed and trimmed[0] in "=+-@＝＋－＠") or value.startswith(("\t", "\r", "\n")) else value


def check_roadmap_activity(call, client, base, tenant, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def mutate(method, path, body, revision=None, key=None):
        return call(method, "/api/v1/" + path, body, tenant, revision=revision, key=key)

    def post(path, body, revision=None, key=None):
        return mutate("POST", path, body, revision, key)

    def reject(method, path, body=None, revision=None, code=None, status=400):
        try:
            mutate(method, path, body, revision)
        except urllib.error.HTTPError as error:
            assert error.code == status, "Unexpected rejection status for " + path
            assert json.loads(error.read())["code"] == code, "Unexpected rejection reason for " + path
            return
        raise AssertionError("An invalid operation was accepted: " + path)

    def download(path, content_type):
        request = urllib.request.Request(base + "/api/v1/" + path,
            headers={"X-Tenant-Id": tenant, "Accept": content_type})
        with client.open(request, timeout=30) as response:
            data = response.read(1_000_001)
            assert len(data) <= 1_000_000 and response.headers.get_content_type() == content_type
            assert response.headers.get("X-Content-Sha256") == hashlib.sha256(data).hexdigest()
            assert response.headers.get("Content-Disposition", "").startswith("attachment;")
            assert "no-store" in response.headers.get("Cache-Control", "")
            return data, response.headers

    def report_query(include_archived=False):
        return urllib.parse.urlencode({"from": period_from, "to": period_to,
            "projectId": project_id, "includeArchived": str(include_archived).lower()})

    def preview(contract):
        return get("maintenance-contracts/" + contract["id"] + "/planning-preview?revision=" + str(contract["revision"]))

    def configure(contract, policy):
        return post("maintenance-contracts/" + contract["id"] + "/configure", {
            "active": True, "automaticPlanning": False, "planningLeadDays": 14,
            "checklistTemplate": contract["checklistTemplate"], "updateWorkdays": True, "workdays": policy}, contract["revision"])

    if previous:
        assert previous["tenantId"] == tenant
        customer_id, contact_id = previous["customerId"], previous["contactId"]
        project_id, task_ids = previous["projectId"], previous["taskIds"]
        contract_id, visit_id = previous["contractId"], previous["visitId"]
        period_from, period_to = previous["from"], previous["to"]
    else:
        # A three-day window includes the business date even near a UTC midnight.
        today = datetime.datetime.now(datetime.timezone.utc).date()
        period_from, period_to = (today - datetime.timedelta(days=1)).isoformat(), (today + datetime.timedelta(days=1)).isoformat()
        customer = post("customers", {"name": "Container contact history customer"})
        customer_id = customer["id"]
        contacts_path = "customers/" + customer_id + "/contacts"
        details = {"name": "Original site contact", "function": "Site office", "email": "history@example.test",
            "phone": "0421 100", "notes": "Synthetic contact", "isPrimary": True}
        key = uuid.uuid4().hex
        book = post(contacts_path, details, customer["revision"], key)
        assert post(contacts_path, details, customer["revision"], key) == book
        contact_id = book["contacts"][0]["id"]
        history_path = contacts_path + "/" + contact_id + "/history"
        original = get(history_path)["items"][0]
        book = post(contacts_path, {**details, "name": "New primary contact"}, book["revision"])
        changed = {**details, "name": "Revised site contact", "phone": "0421 200", "isPrimary": False}
        reject("POST", contacts_path + "/" + contact_id + "/update", changed,
            customer["revision"], "revision_conflict", 409)
        book = post(contacts_path + "/" + contact_id + "/update", changed, book["revision"])
        book = post(contacts_path + "/" + contact_id + "/archive", {}, book["revision"])
        book = post(contacts_path + "/" + contact_id + "/restore", {}, book["revision"])
        retained = get(history_path)
        mutate("DELETE", "customers/" + customer_id, {}, book["revision"])
        assert get(history_path)["items"] == retained["items"]
        assert retained["items"][-1] == original, "Later changes rewrote the original contact"

        business_customer = post("customers", {"name": "Container dependency and workday customer"})
        building = post("buildings", {"customerId": business_customer["id"], "name": "Container roadmap object", "address": "Synthetic site"})
        project = post("projects", {"customerId": business_customer["id"], "buildingId": building["id"],
            "name": "Container activity project", "type": "Renovation", "status": "Planning"})
        project_id = project["id"]
        first = post("tasks", {"projectId": project_id, "title": "Prepare the site", "status": "Open", "dueDate": period_from})
        second = post("tasks", {"projectId": project_id, "title": "Finish the work", "status": "Open", "dueDate": period_to})
        archived = post("tasks", {"projectId": project_id, "title": "Retained previous-period task", "status": "Cancelled",
            "dueDate": (today - datetime.timedelta(days=2)).isoformat()})
        mutate("DELETE", "tasks/" + archived["id"], {}, archived["revision"])
        task_ids = [first["id"], second["id"], archived["id"]]
        key, body = uuid.uuid4().hex, {"predecessorTaskIds": [first["id"]]}
        dependent = mutate("PUT", "tasks/" + second["id"] + "/dependencies", body, second["revision"], key)
        assert mutate("PUT", "tasks/" + second["id"] + "/dependencies", body, second["revision"], key) == dependent
        reject("PUT", "tasks/" + first["id"] + "/dependencies", {"predecessorTaskIds": [second["id"]]},
            first["revision"], "task_dependency_cycle")
        reject("POST", "tasks/" + second["id"] + "/dependency-status", {"status": "InProgress"},
            dependent["revision"], "task_dependency_blocked")
        first = post("tasks/" + first["id"] + "/dependency-status", {"status": "Done"}, first["revision"])
        dependent = post("tasks/" + second["id"] + "/dependency-status", {"status": "InProgress"}, dependent["revision"])
        reject("POST", "tasks/" + first["id"] + "/dependency-status", {"status": "Open"},
            first["revision"], "task_dependency_blocked")
        post("tasks/" + second["id"] + "/dependency-status", {"status": "Done"}, dependent["revision"])

        contract = post("maintenance-contracts", {"customerId": business_customer["id"], "buildingId": building["id"],
            "title": "Container workday maintenance", "scope": "Synthetic roof inspection", "startDate": "2027-01-31",
            "nextDueDate": "2027-01-31", "endDate": "2027-03-31", "intervalMonths": 1, "checklistTemplate": [{"title": "Inspect drain"}]})
        contract_id = contract["id"]
        contract = configure(contract, {"weekdays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], "excludedDates": ["2027-02-01"]})
        reviewed = preview(contract)
        assert reviewed["dueDate"] == "2027-01-31" and reviewed["scheduledDate"] == "2027-02-02"
        schedule = "maintenance-contracts/" + contract_id + "/schedule"
        reject("POST", schedule, {"expectedDueDate": contract["nextDueDate"]}, contract["revision"], "maintenance_workday_preview")
        body, key = {"expectedDueDate": contract["nextDueDate"], "reviewedPreviewDigest": reviewed["digest"]}, uuid.uuid4().hex
        visit = post(schedule, body, contract["revision"], key)
        assert post(schedule, body, contract["revision"], key) == visit
        visit_id = visit["id"]
        original_plan = visit["executionPlan"]
        contract = configure(contract, {"weekdays": ["Friday"], "excludedDates": []})
        reject("POST", schedule, body, contract["revision"], "maintenance_workday_preview")
        existing = preview(contract)
        assert existing["existingVisitId"] == visit_id and existing["scheduledDate"] == "2027-02-02"
        assert existing["workdays"] == original_plan["workdays"]
        assert post(schedule, {**body, "reviewedPreviewDigest": existing["digest"]}, contract["revision"]) == visit
        for check in visit["checklist"]:
            visit = post("maintenance-visits/" + visit_id + "/check", {"checkId": check["id"], "state": "Done", "note": "Synthetic check"}, visit["revision"])
        visit = post("maintenance-visits/" + visit_id + "/complete", {"notes": "Synthetic completed occurrence"}, visit["revision"])
        assert visit["nextDueDate"] == "2027-02-28" and visit["executionPlan"] == original_plan

        old_report = get("reports/business-activity?" + report_query())
        current = get("tasks/" + second["id"])
        mutate("PUT", "tasks/" + second["id"], {**current, "title": '=SUM(1;2)\n"Reviewed task"'}, current["revision"])
        reject("GET", "reports/business-activity/export?" + report_query()
            + "&format=json&expectedDigest=" + old_report["digest"], code="activity_report_changed", status=409)

    customer = get("customers/" + customer_id)
    history = get("customers/" + customer_id + "/contacts/" + contact_id + "/history")
    assert customer["archived"] is True and history["customerRevision"] == customer["revision"]
    assert history["customerId"] == customer_id and history["tenantId"] == tenant and history["contactId"] == contact_id
    assert history["total"] == 5 and history["page"] == 1 and len(history["items"]) == 5
    assert [entry["changeKind"] for entry in history["items"]] == ["Restored", "Archived", "Updated", "PrimaryCleared", "Created"]
    assert history["items"][-1]["before"] is None and history["items"][-1]["after"]["name"] == "Original site contact"
    assert history["items"][2]["before"]["phone"] == "0421 100" and history["items"][2]["after"]["phone"] == "0421 200"
    assert history["current"] == history["items"][0]["after"] and history["current"]["archived"] is False
    for entry in history["items"]:
        assert entry["revision"] == 1 and entry["archived"] is False and entry["recordedByName"]
        assert entry["recordedBy"] == entry["after"]["updatedBy"] and entry["recordedAt"] == entry["after"]["updatedAt"]
        assert len(entry["digest"]) == 64 and entry["tenantId"] == tenant
    for newer, older in zip(history["items"], history["items"][1:]):
        assert newer["before"] == older["after"] and newer["customerRevision"] > older["customerRevision"]
    graph = get("projects/" + project_id + "/task-dependencies")
    tasks = [get("tasks/" + task_id) for task_id in task_ids]
    assert graph["tenantId"] == tenant and graph["projectId"] == project_id
    assert {node["id"] for node in graph["tasks"]} == set(task_ids)
    assert tasks[0]["status"] == tasks[1]["status"] == "Done" and tasks[2]["archived"] is True
    assert tasks[1]["predecessorTaskIds"] == [task_ids[0]] and tasks[0].get("predecessorTaskIds", []) == []
    for node in graph["tasks"]:
        task = next(task for task in tasks if task["id"] == node["id"])
        assert all(node[key] == task[key] for key in ("revision", "title", "status", "archived"))
        assert node["predecessorTaskIds"] == task.get("predecessorTaskIds", [])
    contract, visit = get("maintenance-contracts/" + contract_id), get("maintenance-visits/" + visit_id)
    planned_project = get("projects/" + visit["projectId"])
    assert visit["contractId"] == contract_id and visit["status"] == planned_project["status"] == "Completed"
    assert visit["dueDate"] == "2027-01-31" and visit["executionPlan"]["scheduledDate"] == "2027-02-02"
    assert planned_project["plannedStart"] == planned_project["plannedEnd"] == "2027-02-02"
    assert visit["executionPlan"]["workdays"]["excludedDates"] == ["2027-02-01"]
    assert contract["workdays"] == {"weekdays": ["Friday"], "excludedDates": []} and contract["automaticPlanning"] is False
    assert contract["completedOccurrences"] == 1 and contract["nextDueDate"] == visit["nextDueDate"] == "2027-02-28"
    next_preview = preview(contract)
    assert next_preview["dueDate"] == "2027-02-28" and next_preview["scheduledDate"] == "2027-03-05" and next_preview["existingVisitId"] is None
    assert get("maintenance-visits?contractId=" + contract_id)["total"] == 1
    calendar, _ = download("maintenance-visits/" + visit_id + "/calendar?revision=" + str(visit["revision"]), "text/calendar")
    assert b"DTSTART;VALUE=DATE:20270131\r\n" in calendar and b"DTSTART;VALUE=DATE:20270202" not in calendar

    project = get("projects/" + project_id)
    reports, exports = {}, {}
    for include_archived in (False, True):
        query = report_query(include_archived)
        report = get("reports/business-activity?" + query)
        expected_tasks = tasks if include_archived else tasks[:2]
        assert report["schema"] == "firstblick.business-activity/1.0.0" and report["tenantId"] == tenant
        assert report["filter"] == {"from": period_from, "to": period_to, "projectId": project_id, "includeArchived": include_archived}
        assert report["current"]["totals"] == {"projectsCreated": 1, "tasksCreated": len(expected_tasks), "tasksDue": 2}
        assert report["previous"]["totals"] == {"projectsCreated": 0, "tasksCreated": 0, "tasksDue": int(include_archived)}
        start, end = datetime.date.fromisoformat(period_from), datetime.date.fromisoformat(period_to)
        assert report["current"]["from"] == period_from and report["current"]["to"] == period_to
        assert report["previous"]["from"] == (start - datetime.timedelta(days=(end - start).days + 1)).isoformat()
        assert report["previous"]["to"] == (start - datetime.timedelta(days=1)).isoformat()
        expected_sources = {}
        for kind, record in [("projects", project), *(("tasks", task) for task in expected_tasks)]:
            expected_sources[record["id"]] = {"kind": kind, "id": record["id"], "revision": record["revision"],
                "projectId": project_id, "title": record["name"] if kind == "projects" else record["title"],
                "currentStatus": record["status"], "archived": record["archived"],
                "createdOn": datetime.datetime.fromisoformat(record["createdAt"].replace("Z", "+00:00")).astimezone(ZoneInfo(report["timeZone"])).date().isoformat(),
                "dueDate": record.get("dueDate")}
        assert len(report["sources"]) == len(expected_sources)
        assert {source["id"]: source for source in report["sources"]} == expected_sources
        for period in (report["current"], report["previous"]):
            assert {key: sum(month["counts"][key] for month in period["months"]) for key in period["totals"]} == period["totals"]
        for format_name, content_type in (("csv", "text/csv"), ("json", "application/json")):
            data, headers = download("reports/business-activity/export?" + query + "&format=" + format_name + "&expectedDigest=" + report["digest"], content_type)
            assert headers["X-Report-Digest"] == report["digest"] and headers["X-Report-Tenant"] == tenant
            if format_name == "json":
                assert _snapshot(json.loads(data)) == _snapshot(report)
            else:
                rows = list(csv.reader(io.StringIO(data.decode("utf-8"), newline=""), delimiter=";", strict=True))
                expected_rows = [CSV_HEADER]
                for source in report["sources"]:
                    cells = [source["kind"], source["id"], str(source["revision"]), source["projectId"], source["title"],
                        source["currentStatus"], str(source["archived"]).lower(), source["createdOn"], source["dueDate"] or "",
                        report["current"]["from"], report["current"]["to"], report["previous"]["from"], report["previous"]["to"],
                        report["timeZone"], tenant, report["digest"]]
                    expected_rows.append([_csv_value(cell) for cell in cells])
                assert rows == expected_rows, "CSV does not contain exactly the reviewed report sources"
                exports[str(include_archived).lower()] = hashlib.sha256(data).hexdigest()
        reports[str(include_archived).lower()] = _digest(_snapshot(report))

    evidence = {"tenantId": tenant, "customerId": customer_id, "contactId": contact_id, "projectId": project_id,
        "taskIds": task_ids, "contractId": contract_id, "visitId": visit_id, "from": period_from, "to": period_to,
        "customerSha256": _digest(customer), "contactHistorySha256": _digest(history), "taskGraphSha256": _digest(graph),
        "tasksSha256": _digest(tasks), "contractSha256": _digest(contract), "visitSha256": _digest(visit),
        "plannedProjectSha256": _digest(planned_project), "nextPreviewSha256": _digest(next_preview),
        "calendarSha256": hashlib.sha256(calendar).hexdigest(), "reportSnapshotsSha256": reports, "csvBytesSha256": exports}
    if previous:
        for key, value in evidence.items():
            assert previous[key] == value, "Roadmap evidence changed after restart: " + key
    return {**evidence, "status": "passed", "preservedAcrossRestart": bool(previous), "checks": [
        "contact field originals, actor, primary demotion, archive/restore and replay retained after customer archive",
        "task predecessors reject cycles, premature start and predecessor reopening; successful ordered completion",
        "reviewed workday shift retains its original policy after changes and completes the original monthly recurrence",
        "project-filtered current and comparison periods match exact sources, archive selection and monthly totals",
        "stale report export rejection, CSV formula protection, exact CSV/JSON source binding and downloaded byte hashes",
        "read-only comparison of identities, revisions, frozen evidence, report snapshots and CSV/calendar bytes after restart"]}
