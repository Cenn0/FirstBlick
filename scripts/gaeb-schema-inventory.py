#!/usr/bin/env python3
"""Describe relevant original GAEB schema particles for reproducible adapter maintenance."""
import json
import pathlib
import xml.etree.ElementTree as ET
root = pathlib.Path(__file__).resolve().parents[1] / '.standards' / 'gaeb'
names = {'tgGAEB', 'tgGAEBInfo', 'tgPrjInfo', 'tgAward', 'tgBoQ', 'tgBoQInfo', 'tgBoQBody', 'tgBoQCtgy', 'tgItemlist', 'tgItem', 'tgTotals', 'tgItemTotal', 'tgUP', 'tgCompleteText'}
def describe(node):
    if node.tag.endswith('annotation'):
        return None
    return {'tag': node.tag.split('}')[-1], **node.attrib, 'children': [value for child in node if (value := describe(child)) is not None]}
for file in sorted(root.glob('*.xsd')):
    if not ('_83_' in file.name or '_84_' in file.name or '_Lib_' in file.name):
        continue
    document = ET.fromstring(file.read_bytes())
    print('FILE', file.name, flush=True)
    print('NAMES', [(node.tag.split('}')[-1], node.attrib.get('name')) for node in document.iter() if node.attrib.get('name', '').startswith('tg')], flush=True)
    for node in document.iter():
        if node.attrib.get('name') in names:
            print(json.dumps(describe(node), ensure_ascii=False), flush=True)
