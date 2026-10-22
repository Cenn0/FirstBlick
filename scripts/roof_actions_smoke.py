"""Actual API, versioned PDF storage and restart checks for supplied roof-action cases."""
import hashlib
import json
import math
import urllib.request
import uuid


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def check_roof_actions(call, client, base, tenant, previous=None):
    def get(path):
        return call("GET", "/api/v1/" + path, tenant=tenant)

    def post(path, body, revision=None, key=None):
        return call("POST", "/api/v1/" + path, body, tenant, revision=revision, key=key)

    inputs = {
        "drainage": {"rainfallSource": "Synthetic same-site rainfall pair", "designRainfallLsHa": 300,
            "stormRainfallLsHa": 600, "emergencyScenario": "PrimaryUnavailable", "catchments": [{
                "name": "West", "effectiveAreaM2": 200, "designRunoffCoefficient": .8,
                "primaryCapacityLs": 4, "emergencyCapacityLs": 6, "areaSource": "Synthetic effective projection",
                "capacitySource": "Synthetic capacities at stated water heads and independent flow routes"}]},
        "wind": {"caseSource": "Synthetic single direction", "zones": [{"name": "West", "loadedAreaM2": 12,
            "externalPeakPressureKNm2": .8, "externalCoefficient": -1.5, "internalPeakPressureKNm2": .6,
            "internalCoefficient": .2, "valueSource": "Synthetic pressure, coefficient and area references"}]},
        "snow": {"caseSource": "Synthetic simultaneous arrangement", "groundSnowLoadKNm2": 1.8,
            "exposureCoefficient": .8, "thermalCoefficient": 1, "zones": [{"name": "West", "horizontalAreaM2": 50,
                "shapeCoefficient": .8, "valueSource": "Synthetic non-overlapping horizontal area and shape"}]}}
    if previous:
        assert previous["tenantId"] == tenant
        project_id, records = previous["projectId"], previous["records"]
    else:
        customer = post("customers", {"name": "Synthetic roof-action customer"})
        building = post("buildings", {"customerId": customer["id"], "name": "Synthetic roof", "address": "Synthetic"})
        project = post("projects", {"customerId": customer["id"], "buildingId": building["id"], "name": "Synthetic action cases"})
        project_id, records = project["id"], {}
        for mode, input_value in inputs.items():
            preview = post("technical/roof/" + mode, input_value)
            assert preview["input"] == input_value and preview["provenance"]["isNormative"] is False
            if mode == "drainage":
                row = preview["catchments"][0]
                assert math.isclose(row["primaryDemandLs"], 4.8) and row["creditedPrimaryFlowLs"] == 0
                assert row["emergencyDemandLs"] == 12 and row["emergencyDeficitLs"] == 6
            elif mode == "wind":
                assert math.isclose(preview["zones"][0]["normalForceKN"], -15.84)
            else:
                assert math.isclose(preview["totalVerticalForceKN"], 57.6)
            body = {"projectId": project_id, "roofRevisionId": None, "title": "Synthetic " + mode,
                "method": "roof." + mode, "input": input_value}
            key = uuid.uuid4().hex
            saved = post("technical/runs", body, key=key)
            assert post("technical/runs", body, key=key) == saved
            assert saved["input"] == input_value and saved["result"] == preview and saved["isNormative"] is False
            document = post("technical-results/" + saved["id"] + "/pdf", {}, saved["revision"])
            assert document["sourceId"] == saved["id"] and document["sourceDigest"] == saved["digest"]
            assert document["projectId"] == project_id and document["versionId"] not in (None, "", "null")
            records[mode] = {"id": saved["id"], "recordSha256": _digest(saved), "document": document}

    for mode, evidence in records.items():
        saved = get("technical-results/" + evidence["id"])
        assert saved["projectId"] == project_id and saved["method"] == "roof." + mode
        assert saved["input"] == inputs[mode] and _digest(saved) == evidence["recordSha256"]
        document = evidence["document"]
        request = urllib.request.Request(base + "/api/v1/documents/" + document["id"] + "/download",
            headers={"X-Tenant-Id": tenant, "Accept": "application/pdf"})
        with client.open(request, timeout=30) as response:
            data = response.read(20 * 1024 * 1024 + 1)
            assert data.startswith(b"%PDF-") and len(data) == document["size"]
            digest = hashlib.sha256(data).hexdigest()
            assert digest == document["sha256"] == response.headers.get("X-Content-SHA256")
            assert response.headers.get_content_type() == "application/pdf"
    return {"tenantId": tenant, "projectId": project_id, "records": records,
        "preservedAcrossRestart": bool(previous), "status": "passed"}
