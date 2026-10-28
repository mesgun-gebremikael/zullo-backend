#!/usr/bin/env python3
"""Exercise the real published server in disposable Docker containers only."""
import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

image = os.environ.get('ZULLO_TEST_IMAGE', 'zullo-staging-check:local')
suffix = uuid.uuid4().hex[:12]
network, postgres, api = [f'zullo-smoke-{kind}-{suffix}' for kind in ['net', 'pg', 'api']]

def command(*args, check=True):
    return subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=check)

def request(base, path, body=None, token=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=10) as result:
            return result.status, result.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()

def until(operation, expected, seconds=60):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            status, body = operation()
            if status == expected:
                return body
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.3)
    raise AssertionError(f'Expected HTTP {expected} before the test deadline')

with tempfile.TemporaryDirectory(prefix='zullo-container-') as temporary:
    root = Path(temporary)
    private_key = root / 'synthetic-private.pem'
    command('openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', str(private_key))
    fixture = root / 'synthetic-firebase.json'
    fixture.write_text(json.dumps({
        'type': 'service_account', 'project_id': 'synthetic-zullo-container-check',
        'private_key_id': 'synthetic-key', 'private_key': private_key.read_text(),
        'client_email': 'synthetic@synthetic-zullo-container-check.iam.gserviceaccount.com',
        'client_id': '123456789', 'token_uri': 'https://oauth2.googleapis.com/token',
    }))
    fixture.chmod(0o644) # Test-only credential readable by the container's non-root user.
    env = root / 'server.env'
    env.write_text('\n'.join([
        'ASPNETCORE_ENVIRONMENT=Staging', 'PORT=12345',
        f'ConnectionStrings__Default=postgresql://zullo:test-only@{postgres}:5432/zullo_staging?sslmode=disable',
        'Jwt__Key=' + base64.b64encode(secrets.token_bytes(48)).decode(),
        'Firebase__CredentialsPath=/tmp/synthetic-firebase.json',
    ]) + '\n')
    env.chmod(0o600)
    try:
        command('docker', 'network', 'create', network)
        command('docker', 'run', '-d', '--name', postgres, '--network', network,
                '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '-e', 'POSTGRES_USER=zullo', '-e', 'POSTGRES_DB=zullo_staging', 'postgres:16')
        for _ in range(100):
            if command('docker', 'exec', postgres, 'pg_isready', '-U', 'zullo', '-d', 'zullo_staging', check=False).returncode == 0:
                break
            time.sleep(.3)
        else:
            raise AssertionError('Isolated PostgreSQL did not start')
        command('docker', 'create', '--name', api, '--network', network,
                '-p', '127.0.0.1::12345', '--env-file', str(env), image)
        command('docker', 'cp', str(fixture), api + ':/tmp/synthetic-firebase.json')
        command('docker', 'start', api)
        binding = command('docker', 'port', api, '12345/tcp').stdout.strip()
        base = 'http://' + binding
        until(lambda: request(base, '/health/live'), 200)
        until(lambda: request(base, '/health/ready'), 503)
        assert request(base, '/messages/thread-page')[0] == 401
        assert request(base, '/swagger/index.html')[0] == 404
        uid = command('docker', 'exec', api, 'id', '-u').stdout.strip()
        assert uid.isdigit() and uid != '0', 'Runtime must not run as root'
        settings = json.loads(command('docker', 'exec', api, 'cat', '/app/appsettings.json').stdout)
        assert 'Jwt' not in settings and 'ConnectionStrings' not in settings
        assert command('docker', 'exec', api, 'test', '!', '-d', '/usr/share/dotnet/sdk', check=False).returncode == 0
        assert command('docker', 'exec', api, 'test', '!', '-f', '/usr/local/share/ca-certificates/build-proxy.crt', check=False).returncode == 0
        assert command('docker', 'exec', api, 'test', '!', '-f', '/app/appsettings.Development.json', check=False).returncode == 0
        assert command('docker', 'exec', api, 'test', '!', '-f', '/app/firebase-key.json', check=False).returncode == 0
        for migration_command in ['migrate', '/app/efbundle']:
            command('docker', 'run', '--rm', '--network', network, '--env-file', str(env), image, migration_command)
        until(lambda: request(base, '/health/ready'), 200)
        credentials = {'email': 'synthetic@example.test', 'password': 'Test-only-long-password-123!'}
        status, body = request(base, '/api/auth/register', credentials)
        assert status == 200, 'Real container registration failed'
        status, body = request(base, '/api/auth/login', credentials)
        assert status == 200, 'Real container login failed'
        token = json.loads(body)['token']
        assert request(base, '/matches', token=token)[0] == 200
        assert request(base, '/matches/force-match', {}, token)[0] == 404
        command('docker', 'stop', '--time', '2', postgres)
        until(lambda: request(base, '/health/ready'), 503)
        assert request(base, '/health/live')[0] == 200
        print('PASS: non-root container, custom PORT, no baked development secrets, real startup/JWT/auth, readiness before/after migration, repeat migration, database outage.')
    except Exception:
        # Avoid logging env files, private keys, tokens or complete container logs.
        print('Container check failed; temporary resources will be removed.')
        raise
    finally:
        for name in [api, postgres]:
            command('docker', 'rm', '-f', name, check=False)
        command('docker', 'network', 'rm', network, check=False)
