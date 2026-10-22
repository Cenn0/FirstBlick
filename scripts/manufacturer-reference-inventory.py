#!/usr/bin/env python3
"""Verify public manufacturer originals without redistributing the manuals.

The reports contain hashes and dimension facts only. Copyrighted manuals and extracted
text remain in an ephemeral workspace and are never published as repository files or artifacts.
"""
import hashlib
import http.client
import json
import pathlib
import re
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass


@dataclass(frozen=True)
class ReferenceSource:
    label: str
    url: str
    sha256: str


# Exact bytes retrieved from these original, versioned manufacturer URLs on
# 2026-10-22. A changed upstream document requires a reviewed pin update.
SOURCES = (
    ReferenceSource('BauderPIR FA G20 installation 2025-09', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PRINT/Verlegeanleitungen/VL_Flachdach/BauderPIR_FA_G20_Verlegeanleitung_0925_DE.pdf', 'fe4ed0c62ddf241c65d771a4a7761e826d92779c6317422ece21c84df484cb54'),
    ReferenceSource('BauderPIR FA G20 data 2023-08', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PDB/Flachdach/Daemmstoffe/Dach/BauderPIR_FA_G20_-_Produktdatenblatt_47002000_-_0823_-_DE.pdf', 'de5f910164bc52157035c9cb2c8d208bcd2f6f18dfc95abe94a618488fbda962'),
    ReferenceSource('BauderPIR system brochure 2025-09', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PRINT/Broschueren/Flachdach/FD_Daemmung/BauderPIR_Daemmsystem_0925_DE.pdf', '753c199e4e6751e06845764045bc4907b72397615421b5e41ebd2c2a4710efcf'),
)

MAX_BYTES = 16 * 1024 * 1024
MAX_ATTEMPTS = 3
REQUEST_TIMEOUT_SECONDS = 20
SOURCE_BUDGET_SECONDS = 65
RETRYABLE_STATUS_CODES = {408, 429, 500, 502, 503, 504}


def verify_pdf(data: bytes, source: ReferenceSource) -> None:
    if not data.startswith(b'%PDF-') or len(data) > MAX_BYTES:
        raise ValueError(f'Unexpected manufacturer reference format or size: {source.label}')
    if hashlib.sha256(data).hexdigest() != source.sha256:
        raise ValueError(f'Checksum mismatch for {source.label}; refusing changed upstream data')


def is_retryable(error: Exception) -> bool:
    if isinstance(error, urllib.error.HTTPError):
        return error.code in RETRYABLE_STATUS_CODES
    if isinstance(error, urllib.error.URLError):
        # A certificate failure is not an availability problem. Never disable
        # certificate checks or accept alternate content to obtain a green gate.
        return not isinstance(error.reason, ssl.SSLCertVerificationError)
    return isinstance(error, (TimeoutError, ConnectionError, http.client.IncompleteRead))


def download_reference(source: ReferenceSource, destination: pathlib.Path, *,
                       opener=None, sleep=None, clock=None) -> bytes:
    """Retry transient failures; reuse only verified bytes in the private workspace.

    No persistent CI cache, fallback copy, or manual is published. The caller
    owns the temporary directory, which is removed even when a later check fails.
    Each source has a finite retry and time budget in addition to the job timeout.
    """
    opener = opener or urllib.request.urlopen
    sleep = sleep or time.sleep
    clock = clock or time.monotonic
    if destination.exists():
        if destination.stat().st_size > MAX_BYTES:
            raise ValueError(f'Cached manufacturer reference exceeds size budget: {source.label}')
        data = destination.read_bytes()
        verify_pdf(data, source)
        return data

    deadline = clock() + SOURCE_BUDGET_SECONDS
    request = urllib.request.Request(source.url, headers={'User-Agent': 'FirstBlick-reference-research/1.0'})
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            remaining = deadline - clock()
            if remaining <= 0:
                raise TimeoutError('Manufacturer reference download exhausted its time budget')
            with opener(request, timeout=min(REQUEST_TIMEOUT_SECONDS, remaining)) as response:
                content_length = response.headers.get('Content-Length')
                expected_length = int(content_length) if content_length is not None else None
                if expected_length is not None and not 0 <= expected_length <= MAX_BYTES:
                    raise ValueError(f'Manufacturer reference exceeds size budget: {source.label}')
                chunks = []
                size = 0
                while True:
                    if clock() >= deadline:
                        raise TimeoutError('Manufacturer reference download exhausted its time budget')
                    chunk = response.read1(min(64 * 1024, MAX_BYTES + 1 - size))
                    if not chunk:
                        break
                    chunks.append(chunk)
                    size += len(chunk)
                    if size > MAX_BYTES:
                        raise ValueError(f'Manufacturer reference exceeds size budget: {source.label}')
                data = b''.join(chunks)
                if expected_length is not None and len(data) != expected_length:
                    raise http.client.IncompleteRead(data, max(0, expected_length - len(data)))
            verify_pdf(data, source)
            # Partial or unverified content is never eligible for reuse.
            destination.write_bytes(data)
            return data
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.IncompleteRead) as error:
            delay = 2 ** (attempt - 1)
            if not is_retryable(error) or attempt == MAX_ATTEMPTS or clock() + delay >= deadline:
                raise RuntimeError(f'Could not verify manufacturer original {source.label} from {source.url} after {attempt} attempt(s)') from error
            print(f'Retrying manufacturer original {source.label}: attempt {attempt}/{MAX_ATTEMPTS} failed ({type(error).__name__}); retry in {delay}s', file=sys.stderr, flush=True)
            sleep(delay)
    raise AssertionError('Unreachable download state')


def main() -> None:
    with tempfile.TemporaryDirectory(prefix='firstblick-manufacturer-') as temp:
        for index, source in enumerate(SOURCES):
            path = pathlib.Path(temp) / f'reference-{index}.pdf'
            data = download_reference(source, path)
            text_path = path.with_suffix('.txt')
            subprocess.run(['pdftotext', '-layout', str(path), str(text_path)], check=True, timeout=30)
            text = text_path.read_text()
            facts = {
                'source': source.label, 'url': source.url, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data),
                'dimension_pairs_mm': sorted(set(re.findall(r'\b(?:5|30|55|80|105|130|155|180|205)\s*/\s*(?:30|55|80|105|130|155|180|205|230)\b', text))),
                'board_1200_by_1200_mentioned': bool(re.search(r'1[. ]?200\s*(?:[x×]|mm\s*[x×])\s*1[. ]?200', text)),
                'underlay_100mm_mentioned': bool(re.search(r'100\s*mm.{0,35}(?:Grund|Unter)', text, re.S)),
                'avoid_overlapping_layer_joints_mentioned': 'Fugen der Dämmstoffebenen nicht überlagern' in re.sub(r'\s+', ' ', text)
            }
            print(json.dumps(facts, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
