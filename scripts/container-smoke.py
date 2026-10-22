#!/usr/bin/env python3
"""Exercise the real Compose application without logging credentials, cookies or tokens."""
import datetime
import http.cookiejar
import json
import pathlib
import time
import urllib.parse
import urllib.request
import uuid
from production_assets import check_production_assets
from trade_sources_smoke import check_trade_sources
from equipment_inventory_smoke import check_equipment_and_inventory
from contacts_calendar_originals_smoke import check_contacts_calendar_originals
from customer_vehicle_ledgers_smoke import check_customer_vehicle_ledgers
from project_templates_originals_smoke import check_project_templates_originals
from roadmap_activity_smoke import check_roadmap_activity
from roof_actions_smoke import check_roof_actions

root = pathlib.Path(__file__).resolve().parent.parent
config = {}
for line in root.joinpath(".env").read_text().splitlines():
    if line and not line.startswith("#"):
        key, _, value = line.partition("=")
        config[key] = value.strip("'\"")
base = "http://127.0.0.1:" + config.get("API_PORT", "8080")
cookies = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))


def call(method, path, body=None, tenant=None, revision=None, key=None):
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
        headers["X-XSRF-TOKEN"] = urllib.parse.unquote(next(c.value for c in cookies if c.name == "XSRF-TOKEN"))
        headers["Idempotency-Key"] = key or uuid.uuid4().hex
    if tenant:
        headers["X-Tenant-Id"] = tenant
    if revision is not None:
        headers["If-Match"] = '"' + str(revision) + '"'
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    with client.open(request, timeout=10) as response:
        data = response.read()
        return json.loads(data) if data else None


deadline = time.monotonic() + 120
while True:
    try:
        assert call("GET", "/health/ready")["status"] == "ready"
        break
    except (OSError, AssertionError):
        if time.monotonic() >= deadline:
            raise RuntimeError("API did not become ready within 120 seconds") from None
        time.sleep(2)
assets = check_production_assets(base)
call("GET", "/api/v1/auth/csrf")
assert call("POST", "/api/v1/auth/login", {"email": config["Bootstrap__Email"], "password": config["Bootstrap__Password"]})["authenticated"]
identity = call("GET", "/api/v1/auth/me")
assert len(identity["tenants"]) == 1
tenant = identity["activeTenantId"]
call("GET", "/api/v1/auth/csrf")
folder = root / "artifacts" / "operations"
folder.mkdir(parents=True, exist_ok=True)
previous_path = folder / "container-smoke.json"
previous = json.loads(previous_path.read_text()) if previous_path.exists() else {}
if previous.get("customerId"):
    customer = call("GET", "/api/v1/customers/" + previous["customerId"], tenant=tenant)
else:
    customer = call("POST", "/api/v1/customers", {"name": "Container verification customer"}, tenant)
assert customer["name"] == "Container verification customer"
assert call("GET", "/api/v1/customers/" + customer["id"], tenant=tenant)["name"] == customer["name"]
originals = check_trade_sources(call, client, base, tenant, previous.get("originalSources"))
equipment_inventory = check_equipment_and_inventory(call, tenant, customer, previous.get("equipmentAndInventory"))
contacts_calendar_originals = check_contacts_calendar_originals(call, client, base, tenant, customer, equipment_inventory, previous.get("contactsCalendarOriginals"))
customer_vehicle_ledgers = check_customer_vehicle_ledgers(call, tenant, previous.get("customerVehicleLedgers"))
project_templates_originals = check_project_templates_originals(call, client, base, tenant, previous.get("projectTemplatesOriginals"))
roadmap_activity = check_roadmap_activity(call, client, base, tenant, previous.get("roadmapActivity"))
roof_actions = check_roof_actions(call, client, base, tenant, previous.get("roofActions"))
call("POST", "/api/v1/auth/logout", {})
report = {"status": "passed", "completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "customerId": customer["id"], "previousCustomerPreservedAcrossRestart": bool(previous), "productionAssets": assets, "originalSources": originals, "equipmentAndInventory": equipment_inventory, "contactsCalendarOriginals": contacts_calendar_originals, "customerVehicleLedgers": customer_vehicle_ledgers, "checks": ["database readiness", "production HTML/CSP/screen stylesheet", "CSRF", "bootstrap login", "tenant membership", "authenticated create/read", "original XML persistence, versioned S3 and byte-exact download", "equipment inspection, failed dispatch block, documented reassessment and retained history", "two-hop warehouse chain, supplier return and preserved stock balances", "structured customer contacts, persisted maintenance calendar and original inspection PNG across restart", "retained customer journal, vehicle journeys and costs with independent immutable corrections across restart", "logout"]}
report["projectTemplatesOriginals"] = project_templates_originals
report["checks"].append("reviewed project templates with original task provenance and versioned communication/cost originals across restart")
report["roadmapActivity"] = roadmap_activity
report["roofActions"] = roof_actions
report["checks"].append("supplied wind, snow and drainage cases with immutable project records, versioned PDF originals and exact restart comparison")
report["checks"].append("retained contact changes, ordered task dependencies, reviewed workdays and source-bound activity CSV/JSON across restart")
(folder / "container-smoke.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
