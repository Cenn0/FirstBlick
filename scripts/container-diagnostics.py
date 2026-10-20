#!/usr/bin/env python3
"""Bounded local/CI diagnostics. Never dump container environments or secrets."""
import json
import os
import pathlib
import re
import subprocess


def secret_values():
    values = dict(os.environ)
    config = pathlib.Path('.env')
    if config.is_file():
        for line in config.read_text().splitlines():
            if '=' in line and not line.lstrip().startswith('#'):
                key, value = line.split('=', 1)
                values[key.strip()] = value.strip().strip("\"'")
    return sorted({value for key, value in values.items()
                   if len(value) >= 6 and re.search(r'password|secret|token|access.?key', key, re.I)},
                  key=len, reverse=True)


SECRETS = secret_values()


def redact(value):
    for secret in SECRETS:
        value = value.replace(secret, '[redacted]')
    return re.sub(r'(?i)(password|secretkey|accesskey|token)=([^;\s]+)', r'\1=[redacted]', value)


def run(*args):
    try:
        result = subprocess.run(args, text=True, capture_output=True, timeout=30, check=False)
        return result.returncode, result.stdout, result.stderr
    except (OSError, subprocess.TimeoutExpired) as error:
        return 1, '', type(error).__name__


code, ids, error = run('docker', 'compose', 'ps', '--all', '--quiet')
report = {'containers': [], 'errors': [], 'fileModes': {}}
if code:
    report['errors'].append(redact(error[-4000:]))
for container_id in ids.split()[:20]:
    code, content, error = run('docker', 'inspect', container_id)
    if code:
        report['errors'].append(redact(error[-4000:]))
        continue
    for item in json.loads(content):
        state = item.get('State', {})
        health = state.get('Health', {})
        report['containers'].append({
            'name': item.get('Name'), 'imageId': item.get('Image'),
            'status': state.get('Status'), 'exitCode': state.get('ExitCode'),
            'oomKilled': state.get('OOMKilled'), 'restartCount': item.get('RestartCount'),
            'error': redact(str(state.get('Error', ''))[-4000:]),
            'health': {'status': health.get('Status'), 'failingStreak': health.get('FailingStreak'),
                       'log': [{key: redact(str(record[key]))[-4000:] for key in ('Start', 'End', 'ExitCode', 'Output') if key in record}
                               for record in health.get('Log', [])[-5:]]}})
for name in ('secrets', 'secrets/s3.json', 'secrets/data-protection.pfx', '.env'):
    path = pathlib.Path(name)
    if path.exists():
        stat = path.stat()
        report['fileModes'][name] = {'mode': oct(stat.st_mode & 0o777), 'uid': stat.st_uid, 'gid': stat.st_gid}
code, logs, error = run('docker', 'compose', 'logs', '--no-color', '--tail=100', 'db', 's3', 'api', 'worker', 'migrate', 'storage-init')
report['logs'] = redact(logs[-200000:])
if code:
    report['errors'].append(redact(error[-4000:]))
print(json.dumps(report, indent=2))
