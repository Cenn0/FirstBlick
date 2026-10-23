#!/usr/bin/env python3
"""Exercise operator-created tenant isolation in the ephemeral CI Compose stack."""
import http.cookiejar
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
config = {}
for line in (root / '.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, _, value = line.partition('=')
        config[key] = value.strip("'\"")
base = os.environ.get('FIRSTBLICK_SMOKE_BASE_URL',
                      'http://127.0.0.1:' + config.get('API_PORT', '8080')).rstrip('/')
target = urllib.parse.urlsplit(base)
if target.username or target.password or target.query or target.fragment or target.path or not target.hostname:
    raise ValueError('Smoke origin must be an explicit origin without credentials or path')
if target.scheme != 'https' and not (target.scheme == 'http' and target.hostname in {'localhost', '127.0.0.1', '::1'}):
    raise ValueError('Non-loopback smoke origins require verified HTTPS')

cookies = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))


def call(method, path, body=None, tenant=None, key=None):
    headers = {'Accept': 'application/json'}
    if body is not None:
        headers['Content-Type'] = 'application/json'
        headers['X-XSRF-TOKEN'] = urllib.parse.unquote(next(c.value for c in cookies if c.name == 'XSRF-TOKEN'))
        headers['Idempotency-Key'] = key or uuid.uuid4().hex
    if tenant:
        headers['X-Tenant-Id'] = tenant
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(),
                                     headers=headers, method=method)
    with client.open(request, timeout=15) as response:
        data = response.read()
        return (json.loads(data) if data else None), response.headers


def denied(path, tenant):
    try:
        call('GET', path, tenant=tenant)
    except urllib.error.HTTPError as error:
        assert error.code in (403, 404), 'Foreign business access was not rejected'
    else:
        raise AssertionError('Foreign business access was allowed')


original = json.loads((root / 'artifacts' / 'operations' / 'container-smoke.json').read_text())
assert original['status'] == 'passed' and original['tenantId'] and original['customerId']
call('GET', '/api/v1/auth/csrf')
assert call('POST', '/api/v1/auth/login',
            {'email': config['Bootstrap__Email'], 'password': config['Bootstrap__Password']})[0]['authenticated']
call('GET', '/api/v1/auth/csrf')
identity = call('GET', '/api/v1/auth/me')[0]
assert identity['canCreateBusiness'] is True
assert len(identity['tenants']) == 1 and identity['tenants'][0]['id'] == original['tenantId']
assert identity['tenants'][0]['role'] == 'ManagingDirector'

body = {'name': 'Second synthetic CI business', 'timeZone': 'UTC'}
key = uuid.uuid4().hex
created, _ = call('POST', '/api/v1/businesses', body, tenant=original['tenantId'], key=key)
assert created['id'] != original['tenantId'] and created['name'] == body['name']
replay, replay_headers = call('POST', '/api/v1/businesses', body, tenant=original['tenantId'], key=key)
assert replay['id'] == created['id'] and replay_headers.get('Idempotency-Replayed') == 'true'

after = call('GET', '/api/v1/auth/me')[0]
assert len(after['tenants']) == 2
assert next(x for x in after['tenants'] if x['id'] == created['id'])['role'] == 'ManagingDirector'
assert call('GET', '/api/v1/customers?page=1&pageSize=25', tenant=created['id'])[0]['total'] == 0
denied('/api/v1/customers/' + original['customerId'], created['id'])
new_customer, _ = call('POST', '/api/v1/customers', {'name': 'New tenant synthetic customer'}, tenant=created['id'])
denied('/api/v1/customers/' + new_customer['id'], original['tenantId'])
call('POST', '/api/v1/auth/logout', {})

result = {'status': 'passed', 'operatorClaim': True, 'businessesAfterCreation': 2,
          'idempotentReplay': True, 'newBusinessInitiallyEmpty': True,
          'foreignRecordsDeniedInBothDirections': True}
(root / 'artifacts' / 'operations' / 'business-creation.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result))
