#!/usr/bin/env python3
"""Require a nonempty, completely passing domain acceptance TRX inventory."""
import pathlib
import sys
import xml.etree.ElementTree as ET

CLASS = "FirstBlick.Core.Tests.Technical.DomainAcceptanceGateTests."
MINIMUM_CASES = 11
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def verify(path):
    path = pathlib.Path(path)
    if not path.is_file() or not 0 < path.stat().st_size <= 5 * 1024 * 1024:
        raise ValueError("Domain TRX is missing, empty or exceeds its budget")
    root = ET.parse(path).getroot()
    summary = root.find("t:ResultSummary", NS)
    counters = root.find("t:ResultSummary/t:Counters", NS)
    results = root.findall("t:Results/t:UnitTestResult", NS)
    if summary is None or summary.get("outcome") != "Completed" or counters is None:
        raise ValueError("Domain TRX did not complete successfully")
    total = len(results)
    if total < MINIMUM_CASES or len({x.get("executionId") for x in results}) != total:
        raise ValueError("Domain gate requires at least eleven distinct executed reference cases")
    if any(x.get("outcome") != "Passed" or not x.get("testName", "").startswith(CLASS) for x in results):
        raise ValueError("Domain gate contains a failed, skipped or unrelated case")
    for name in ("total", "executed", "passed"):
        if int(counters.get(name, "-1")) != total:
            raise ValueError("Domain TRX counters disagree with actual results")
    for name in ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted"):
        if int(counters.get(name, "-1")) != 0:
            raise ValueError("Domain TRX contains unsuccessful or unexecuted cases")
    return total


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: verify-domain-gate.py <domain-acceptance.trx>")
    count = verify(sys.argv[1])
    print(f"Verified {count} executed, passing domain acceptance cases")
