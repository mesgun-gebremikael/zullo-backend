#!/usr/bin/env python3
"""Exercise the real published server in disposable Docker containers only."""
from concurrent.futures import ThreadPoolExecutor
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
        'ASPNETCORE_ENVIRONMENT=Staging', 'PORT=12345', 'Deployment__ExpectedDatabaseName=zullo_staging',
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
        def start_api(*extra_env):
            arguments = ['docker', 'create', '--name', api, '--network', network,
                         '-p', '127.0.0.1::12345', '--env-file', str(env)]
            for value in extra_env:
                arguments += ['-e', value]
            command(*arguments, image)
            command('docker', 'cp', str(fixture), api + ':/tmp/synthetic-firebase.json')
            command('docker', 'start', api)
            return 'http://' + command('docker', 'port', api, '12345/tcp').stdout.strip()

        base = start_api()
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
        wrong_target = root / 'wrong-target.env'
        wrong_target.write_text(env.read_text().replace('Deployment__ExpectedDatabaseName=zullo_staging',
                                                       'Deployment__ExpectedDatabaseName=different_project_db'))
        wrong_target.chmod(0o600)
        rejected = command('docker', 'run', '--rm', '--network', network, '--env-file', str(wrong_target), image, 'migrate', check=False)
        assert rejected.returncode != 0, 'Migration must reject the wrong database target'
        assert request(base, '/health/ready')[0] == 503, 'Rejected migration must not upgrade the database'
        for migration_command in ['migrate', '/app/efbundle']:
            command('docker', 'run', '--rm', '--network', network, '--env-file', str(env), image, migration_command)
        until(lambda: request(base, '/health/ready'), 200)
        credentials = {'email': 'synthetic@example.test', 'password': 'Test-only-long-password-123!'}
        status, body = request(base, '/api/auth/register', credentials)
        assert status == 200, 'Real container registration failed'
        # Real PostgreSQL race: one account and no leaked token/server error for losers.
        race_credentials = {'email': 'parallel@example.test', 'password': 'Test-only-long-password-123!'}
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda _: request(base, '/api/auth/register', race_credentials), range(4)))
        assert sorted(status for status, _ in results) == [200, 400, 400, 400], 'Parallel registration must not return 500'
        for status, body in results:
            if status == 400:
                assert 'token' not in json.loads(body), 'Rejected duplicate must never receive existing account credentials'
        rows = command('docker', 'exec', postgres, 'psql', '-U', 'zullo', '-d', 'zullo_staging',
                       '-tAc', "SELECT count(*) FROM \"User\" WHERE \"Email\" = 'parallel@example.test'").stdout.strip()
        assert rows == '1', 'The email uniqueness constraint must remain enabled'
        status, body = request(base, '/api/auth/login', credentials)
        assert status == 200, 'Real container login failed'
        token = json.loads(body)['token']
        assert request(base, '/matches', token=token)[0] == 200
        assert request(base, '/matches/force-match', {}, token)[0] == 404
        command('docker', 'stop', '--time', '2', postgres)
        until(lambda: request(base, '/health/ready'), 503)
        assert request(base, '/health/live')[0] == 200
        # Explicit free-tier mode: migrate before listening, and keep data on a cold restart.
        command('docker', 'rm', '-f', api)
        command('docker', 'start', postgres)
        for _ in range(100):
            if command('docker', 'exec', postgres, 'pg_isready', '-U', 'zullo', '-d', 'zullo_staging', check=False).returncode == 0:
                break
            time.sleep(.3)
        command('docker', 'exec', postgres, 'psql', '-U', 'zullo', '-d', 'postgres', '-c', 'DROP DATABASE zullo_staging WITH (FORCE)')
        command('docker', 'exec', postgres, 'psql', '-U', 'zullo', '-d', 'postgres', '-c', 'CREATE DATABASE zullo_staging')
        for overrides, expected_error in [
            (['ASPNETCORE_ENVIRONMENT=Production'], 'only for Staging'),
            (['Deployment__ExpectedDatabaseName=other_project'], 'only for Staging'),
            ([], 'Firebase Admin Secret File'),
        ]:
            arguments = ['docker', 'run', '--rm', '--network', network, '--env-file', str(env),
                         '-e', 'ZULLO_STAGING_MIGRATE_ON_START=true']
            for value in overrides:
                arguments += ['-e', value]
            rejected = command(*arguments, image, check=False)
            assert rejected.returncode != 0 and expected_error in rejected.stderr, 'Unsafe startup migration must be refused'
        base = start_api('ZULLO_STAGING_MIGRATE_ON_START=true')
        until(lambda: request(base, '/health/ready'), 200)
        assert request(base, '/api/auth/register', credentials)[0] == 200
        command('docker', 'restart', api)
        base = 'http://' + command('docker', 'port', api, '12345/tcp').stdout.strip()
        until(lambda: request(base, '/health/ready'), 200)
        assert request(base, '/api/auth/login', credentials)[0] == 200, 'Cold restart must preserve the test account'
        command('docker', 'stop', '--time', '2', postgres)
        command('docker', 'restart', api)
        for _ in range(100):
            if command('docker', 'inspect', '-f', '{{.State.Running}}', api).stdout.strip() == 'false':
                break
            time.sleep(.3)
        else:
            raise AssertionError('Migration failure must stop server startup')
        assert command('docker', 'inspect', '-f', '{{.State.ExitCode}}', api).stdout.strip() != '0'
        print('PASS: non-root container, custom PORT, no baked development secrets, real startup/JWT/auth, readiness before/after migration, repeat migration, database outage; opt-in free staging startup, production/wrong-target/missing-key guards, cold restart and failure before listening.')
    except Exception:
        print('Container check failed; temporary resources will be removed.')
        diagnostic = command('docker', 'logs', '--tail', '30', api, check=False)
        safe_log = diagnostic.stdout + diagnostic.stderr
        for line in env.read_text().splitlines():
            if line.startswith('Jwt__Key='):
                safe_log = safe_log.replace(line.split('=', 1)[1], '[redacted]')
        safe_log = safe_log.replace(private_key.read_text(), '[synthetic private key redacted]')
        print(safe_log)

        raise
    finally:
        for name in [api, postgres]:
            command('docker', 'rm', '-f', name, check=False)
        command('docker', 'network', 'rm', network, check=False)
