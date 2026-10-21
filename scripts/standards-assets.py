#!/usr/bin/env python3
"""Install original, hash-pinned validation assets. Never send business XML to a server."""
import argparse
import hashlib
import io
import json
import pathlib
import stat
import urllib.request
import zipfile
import xml.etree.ElementTree as ET

ASSETS = [
    ('xrechnung', 'https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/download/v2026-08-31/xrechnung-3.0.2-validator-configuration-2026-08-31.zip', '2530cd107c414511c5d0462ec10f886910395abfca820db82e83d70bf01221a8'),
    ('validator.jar', 'https://github.com/itplr-kosit/validator/releases/download/v1.6.3/validator-1.6.3-standalone.jar', '799e64befca97d4080e03608c80b85dd5a5ecc5f4ae4f35d1116ec2855b9a7c9'),
    ('gaeb', 'https://www.gaeb.de/wp-content/uploads/2021/06/2021-05_Leistungsverzeichnis.zip', 'd4f19a79c46119ba6a7bbca1067bc2a26ccadb60d1b5fd8a82725f45b491e203'),
]

def download(url: str) -> bytes:
    request = urllib.request.Request(url, headers={'User-Agent': 'FirstBlick-reference-validation/1.0'})
    with urllib.request.urlopen(request, timeout=60) as response:
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
    parser.add_argument('--inventory', action='store_true')
    parser.add_argument('--target', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1] / '.standards')
    args = parser.parse_args()
    root = args.target.resolve()
    root.mkdir(parents=True, exist_ok=True)
    inventory = []
    for name, url, expected in ASSETS:
        destination = root / (name if name.endswith('.jar') else name + '.zip')
        data = destination.read_bytes() if destination.exists() else download(url)
        digest = hashlib.sha256(data).hexdigest()
        if digest != expected:
            raise ValueError(f'Checksum mismatch for {name}; refusing changed upstream data')
        destination.write_bytes(data)
        inventory.append({'name': name, 'sourceUrl': url, 'sha256': digest, 'size': len(data)})
        print('PINNED_ASSET', json.dumps(inventory[-1]), flush=True)
        if not name.endswith('.jar'):
            extract(data, root / name)
    (root / 'inventory.json').write_text(json.dumps(inventory, indent=2) + '\n')
    if args.inventory:
        for file in sorted((root / 'gaeb').glob('*.xsd')):
            if '_83_' in file.name or '_84_' in file.name:
                document = ET.fromstring(file.read_bytes())
                print('SCHEMA', file.name, document.attrib, flush=True)
                for node in document:
                    if node.tag.endswith('annotation'):
                        continue
                    print('DECLARATION', node.tag.split('}')[-1], node.attrib)
                    if node.attrib.get('name', '').lower() in ('gaeb', 'award', 'boq', 'item', 'titem', 'taward', 'tgaeb', 'tboq', 'tboqbody', 'titemlist'):
                        print(ET.tostring(node, encoding='unicode')[:15000])

if __name__ == '__main__':
    main()
