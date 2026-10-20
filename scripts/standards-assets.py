#!/usr/bin/env python3
"""Download original validation assets for inspection. No user documents are transmitted.

The one-time GAEB inventory records the vendor archive hash before it is pinned for
application builds. Inventory mode is not an application validation or trust verdict.
"""
import argparse
import hashlib
import io
import json
import pathlib
import stat
import urllib.request
import zipfile
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1] / '.standards'
ASSETS = [
    ('xrechnung', 'https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/download/v2026-08-31/xrechnung-3.0.2-validator-configuration-2026-08-31.zip', '2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8'),
    ('validator.jar', 'https://github.com/itplr-kosit/validator/releases/download/v1.6.3/validator-1.6.3-standalone.jar', '799e64befca97d4080e03608c80b85dd5a5ecc5f4ae4f35d1116ec2855b9a7c9'),
    ('gaeb', 'https://www.gaeb.de/wp-content/uploads/2021/06/2021-05_Leistungsverzeichnis.zip', None),
]

def download(url: str) -> bytes:
    req = urllib.request.Request(url, headers={'User-Agent': 'FirstBlick-reference-validation/1.0'})
    with urllib.request.urlopen(req, timeout=60) as response:
        data = response.read(64 * 1024 * 1024 + 1)
    if len(data) > 64 * 1024 * 1024:
        raise ValueError('Asset download exceeds fixed budget')
    return data

def extract(data: bytes, target: pathlib.Path) -> None:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) > 2000 or sum(e.file_size for e in entries) > 256 * 1024 * 1024:
            raise ValueError('Archive exceeds file or size budget')
        for entry in entries:
            path = pathlib.PurePosixPath(entry.filename)
            if path.is_absolute() or '..' in path.parts or '\\' in entry.filename or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError('Unsafe archive path')
            destination = target.joinpath(*path.parts)
            if entry.is_dir():
                destination.mkdir(parents=True, exist_ok=True)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(archive.read(entry))

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--inventory', action='store_true', required=True)
    parser.parse_args()
    ROOT.mkdir(exist_ok=True)
    inventory = []
    for name, url, expected in ASSETS:
        data = download(url)
        digest = hashlib.sha256(data).hexdigest()
        if expected is not None and digest != expected:
            raise ValueError(f'Checksum mismatch for {name}')
        inventory.append({'name': name, 'sourceUrl': url, 'sha256': digest, 'size': len(data), 'upstreamDigestVerified': expected is not None})
        print('ASSET', json.dumps(inventory[-1]), flush=True)
        if name.endswith('.jar'):
            (ROOT / name).write_bytes(data)
        else:
            extract(data, ROOT / name)
            for file in sorted((ROOT / name).rglob('*')):
                if file.is_file():
                    print('RESOURCE', file.relative_to(ROOT), file.stat().st_size)
    (ROOT / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')
    for file in (ROOT / 'xrechnung').rglob('scenarios.xml'):
        print('SCENARIOS', file.read_text(encoding='utf-8')[:24000])
    for file in (ROOT / 'gaeb').rglob('*.xsd'):
        if 'DA84' in file.name or 'DA83' in file.name:
            tree = ET.fromstring(file.read_bytes())
            print('SCHEMA', file.relative_to(ROOT), tree.attrib)
            for node in tree:
                if node.attrib.get('name') in ('GAEB', 'Award', 'BoQ', 'Item', 'ItemType', 'tItem', 'tAward', 'tGAEB', 'tBoQ'):
                    print(ET.tostring(node, encoding='unicode')[:18000])

if __name__ == '__main__':
    main()
