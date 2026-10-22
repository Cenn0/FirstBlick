"""Deterministic transfer-failure tests; fixtures contain no manufacturer content."""
import hashlib
import importlib.util
import io
from pathlib import Path
import ssl
import sys
import tempfile
import unittest
import urllib.error
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    'manufacturer_reference_inventory',
    Path(__file__).with_name('manufacturer-reference-inventory.py'),
)
inventory = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = inventory
SPEC.loader.exec_module(inventory)

PDF = b'%PDF-1.4\nsynthetic transfer fixture only\n%%EOF\n'
SOURCE = inventory.ReferenceSource('synthetic fixture', 'https://manufacturer.example/reference.pdf', hashlib.sha256(PDF).hexdigest())


class Response(io.BytesIO):
    def __init__(self, data=PDF, *, content_length=True):
        super().__init__(data)
        self.headers = {'Content-Length': str(len(data))} if content_length else {}


class Opener:
    def __init__(self, *outcomes):
        self.outcomes = iter(outcomes)
        self.calls = []

    def __call__(self, request, *, timeout):
        self.calls.append((request.full_url, timeout))
        outcome = next(self.outcomes)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


class Clock:
    def __init__(self):
        self.now = 0
        self.delays = []

    def __call__(self):
        return self.now

    def sleep(self, delay):
        self.delays.append(delay)
        self.now += delay


class DownloadReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.destination = Path(self.temp.name) / 'original.pdf'
        self.clock = Clock()

    def download(self, opener):
        return inventory.download_reference(SOURCE, self.destination, opener=opener, sleep=self.clock.sleep, clock=self.clock)

    def test_success_keeps_exact_original_and_uses_bounded_request(self):
        opener = Opener(Response())
        self.assertEqual(PDF, self.download(opener))
        self.assertEqual(PDF, self.destination.read_bytes())
        self.assertEqual([(SOURCE.url, inventory.REQUEST_TIMEOUT_SECONDS)], opener.calls)

    def test_transient_http_failure_then_timeout_recovers(self):
        error = urllib.error.HTTPError(SOURCE.url, 503, 'unavailable', {}, None)
        opener = Opener(error, urllib.error.URLError(TimeoutError('connect timeout')), Response())
        self.assertEqual(PDF, self.download(opener))
        self.assertEqual([1, 2], self.clock.delays)
        self.assertEqual(3, len(opener.calls))

    def test_body_timeout_discards_partial_bytes_before_retry(self):
        class FailingResponse(Response):
            def read1(self, size):
                if self.tell():
                    raise TimeoutError('body timeout')
                return super().read1(8)

        opener = Opener(FailingResponse(), Response())
        self.assertEqual(PDF, self.download(opener))
        self.assertEqual(PDF, self.destination.read_bytes())
        self.assertEqual([1], self.clock.delays)

    def test_exhausted_failures_are_errors_and_leave_no_cached_content(self):
        opener = Opener(*(TimeoutError('timeout') for _ in range(inventory.MAX_ATTEMPTS)))
        with self.assertRaisesRegex(RuntimeError, 'after 3 attempt'):
            self.download(opener)
        self.assertFalse(self.destination.exists())
        self.assertEqual(inventory.MAX_ATTEMPTS, len(opener.calls))

    def test_time_budget_stops_a_stalled_read_without_starting_another_attempt(self):
        clock = self.clock

        class SlowResponse(Response):
            def read1(self, size):
                clock.now += inventory.SOURCE_BUDGET_SECONDS
                return super().read1(8)

        opener = Opener(SlowResponse())
        with self.assertRaises(RuntimeError):
            self.download(opener)
        self.assertEqual(1, len(opener.calls))
        self.assertEqual([], self.clock.delays)
        self.assertFalse(self.destination.exists())

    def test_truncated_content_length_is_retried(self):
        truncated = Response(PDF[:8])
        truncated.headers['Content-Length'] = str(len(PDF))
        opener = Opener(truncated, Response())
        self.assertEqual(PDF, self.download(opener))
        self.assertEqual(2, len(opener.calls))

    def test_nontransient_http_error_is_not_retried(self):
        opener = Opener(urllib.error.HTTPError(SOURCE.url, 404, 'not found', {}, None))
        with self.assertRaises(RuntimeError):
            self.download(opener)
        self.assertEqual(1, len(opener.calls))
        self.assertEqual([], self.clock.delays)

    def test_certificate_failure_is_not_retried_or_ignored(self):
        opener = Opener(urllib.error.URLError(ssl.SSLCertVerificationError('untrusted certificate')))
        with self.assertRaises(RuntimeError):
            self.download(opener)
        self.assertEqual(1, len(opener.calls))
        self.assertFalse(self.destination.exists())

    def test_changed_pdf_is_rejected_without_retries_or_cache_write(self):
        opener = Opener(Response(PDF + b'changed'))
        with self.assertRaisesRegex(ValueError, 'Checksum mismatch'):
            self.download(opener)
        self.assertEqual(1, len(opener.calls))
        self.assertFalse(self.destination.exists())

    def test_non_pdf_is_rejected_even_when_hash_matches(self):
        data = b'<html>server error</html>'
        source = inventory.ReferenceSource(SOURCE.label, SOURCE.url, hashlib.sha256(data).hexdigest())
        with self.assertRaisesRegex(ValueError, 'format or size'):
            inventory.verify_pdf(data, source)

    def test_oversized_content_length_is_rejected_before_reading(self):
        response = Response()
        response.headers['Content-Length'] = str(inventory.MAX_BYTES + 1)
        opener = Opener(response)
        with self.assertRaisesRegex(ValueError, 'size budget'):
            self.download(opener)
        self.assertEqual(1, len(opener.calls))
        self.assertFalse(self.destination.exists())

    def test_oversized_body_without_content_length_is_rejected(self):
        opener = Opener(Response(content_length=False))
        with patch.object(inventory, 'MAX_BYTES', 12):
            with self.assertRaisesRegex(ValueError, 'size budget'):
                self.download(opener)
        self.assertFalse(self.destination.exists())

    def test_verified_private_workspace_bytes_can_be_reused_without_network(self):
        self.destination.write_bytes(PDF)
        opener = Opener()
        self.assertEqual(PDF, self.download(opener))
        self.assertEqual([], opener.calls)

    def test_corrupt_private_workspace_bytes_are_never_trusted(self):
        self.destination.write_bytes(PDF + b'changed')
        opener = Opener()
        with self.assertRaisesRegex(ValueError, 'Checksum mismatch'):
            self.download(opener)
        self.assertEqual([], opener.calls)

    def test_oversized_private_workspace_file_is_rejected_before_read(self):
        self.destination.write_bytes(PDF)
        with patch.object(inventory, 'MAX_BYTES', 12):
            with self.assertRaisesRegex(ValueError, 'size budget'):
                self.download(Opener())


if __name__ == '__main__':
    unittest.main()
