import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("domain_gate", Path(__file__).with_name("verify-domain-gate.py"))
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class DomainGateTests(unittest.TestCase):
    def trx(self, count=11, outcome="Passed", counters=None, prefix=gate.CLASS):
        values = dict(total=count, executed=count, passed=count, failed=0, error=0, timeout=0, aborted=0, inconclusive=0, notExecuted=0)
        values.update(counters or {})
        attributes = " ".join(f'{key}="{value}"' for key, value in values.items())
        results = "".join(f'<UnitTestResult executionId="{i}" testName="{prefix}Case{i}" outcome="{outcome}"/>' for i in range(count))
        return f'<TestRun xmlns="{gate.NS["t"]}"><Results>{results}</Results><ResultSummary outcome="Completed"><Counters {attributes}/></ResultSummary></TestRun>'

    def check(self, content):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "domain.trx"
            path.write_text(content, encoding="utf-8")
            return gate.verify(path)

    def test_complete_reference_inventory_passes(self):
        self.assertEqual(11, self.check(self.trx()))

    def test_zero_or_incomplete_selection_fails(self):
        for count in (0, 1, 10):
            with self.subTest(count=count), self.assertRaises(ValueError):
                self.check(self.trx(count))

    def test_failed_or_skipped_case_fails(self):
        for outcome in ("Failed", "NotExecuted"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.check(self.trx(outcome=outcome))

    def test_unrelated_tests_cannot_satisfy_inventory(self):
        with self.assertRaises(ValueError):
            self.check(self.trx(prefix="Different.Tests."))

    def test_counters_must_match_actual_results(self):
        with self.assertRaises(ValueError):
            self.check(self.trx(counters={"passed": 12}))

    def test_missing_trx_fails(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            gate.verify(Path(directory) / "absent.trx")


if __name__ == "__main__":
    unittest.main()
