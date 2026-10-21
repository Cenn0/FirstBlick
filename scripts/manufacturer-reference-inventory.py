#!/usr/bin/env python3
"""Verify public manufacturer originals without redistributing the manuals.

The reports contain hashes and dimension facts only. Copyrighted manuals and extracted
text remain in an ephemeral workspace and are never published as repository files or artifacts.
"""
import hashlib
import json
import pathlib
import re
import subprocess
import tempfile
import urllib.request

SOURCES = [
    ('BauderPIR FA G20 installation 2025-09', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PRINT/Verlegeanleitungen/VL_Flachdach/BauderPIR_FA_G20_Verlegeanleitung_0925_DE.pdf'),
    ('BauderPIR FA G20 data 2023-08', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PDB/Flachdach/Daemmstoffe/Dach/BauderPIR_FA_G20_-_Produktdatenblatt_47002000_-_0823_-_DE.pdf'),
    ('BauderPIR system brochure 2025-09', 'https://www.bauder.de/fileadmin/bauder.de/DOWNLOADS/PRINT/Broschueren/Flachdach/FD_Daemmung/BauderPIR_Daemmsystem_0925_DE.pdf'),
]
with tempfile.TemporaryDirectory(prefix='firstblick-manufacturer-') as temp:
    for index, (label, url) in enumerate(SOURCES):
        request = urllib.request.Request(url, headers={'User-Agent': 'FirstBlick-reference-research/1.0'})
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read(16 * 1024 * 1024 + 1)
        if not data.startswith(b'%PDF-') or len(data) > 16 * 1024 * 1024:
            raise ValueError('Unexpected manufacturer reference format or size')
        path = pathlib.Path(temp) / f'reference-{index}.pdf'; path.write_bytes(data)
        text_path = path.with_suffix('.txt')
        subprocess.run(['pdftotext', '-layout', str(path), str(text_path)], check=True, timeout=30)
        text = text_path.read_text()
        facts = {
            'source': label, 'url': url, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data),
            'dimension_pairs_mm': sorted(set(re.findall(r'\b(?:5|30|55|80|105|130|155|180|205)\s*/\s*(?:30|55|80|105|130|155|180|205|230)\b', text))),
            'board_1200_by_1200_mentioned': bool(re.search(r'1[. ]?200\s*(?:[x×]|mm\s*[x×])\s*1[. ]?200', text)),
            'underlay_100mm_mentioned': bool(re.search(r'100\s*mm.{0,35}(?:Grund|Unter)', text, re.S)),
            'avoid_overlapping_layer_joints_mentioned': 'Fugen der Dämmstoffebenen nicht überlagern' in re.sub(r'\s+', ' ', text)
        }
        print(json.dumps(facts, ensure_ascii=False), flush=True)
