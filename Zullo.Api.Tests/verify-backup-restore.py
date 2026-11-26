#!/usr/bin/env python3
"""Restore drill using only this script's disposable PG16 and synthetic EF data.

No database connection, backup file or production resource is accepted as input.
Only the container and temporary directory created by this invocation are removed.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import secrets
import subprocess
import tempfile
import time
import uuid

argparse.ArgumentParser(description='Restore only synthetic data in an owned disposable PostgreSQL container.').parse_args()
root = Path(__file__).resolve().parents[1]
drill_id = uuid.uuid4().hex
container = 'zullo-restore-drill-' + drill_id
source_database = 'zullo_restore_source_' + drill_id
target_database = 'zullo_restore_target_' + drill_id
password = secrets.token_urlsafe(24)
env = dict(os.environ, POSTGRES_PASSWORD=password, PGPASSWORD=password)
started = time.monotonic()


def run(command, *, check=True, process_env=None, timeout=300):
    result = subprocess.run(command, env=process_env or env, text=True,
                            capture_output=True, timeout=timeout)
    if check and result.returncode:
        # All inputs are synthetic, but do not print even the disposable password.
        details = (result.stdout + result.stderr).replace(password, '[redacted]')
        raise RuntimeError(details[-10000:])
    return result


def docker(*arguments, check=True):
    return run(['docker', *arguments], check=check)


created = False
try:
    with tempfile.TemporaryDirectory(prefix='zullo-restore-drill-') as temporary:
        directory = Path(temporary)
        docker('run', '--detach', '--name', container,
               '--publish', '127.0.0.1::5432', '--env', 'POSTGRES_PASSWORD',
               '--env', 'POSTGRES_USER=zullo', '--env', f'POSTGRES_DB={source_database}', 'postgres:16')
        created = True
        for attempt in range(60):
            if docker('exec', container, 'pg_isready', '-U', 'zullo', '-d', source_database, check=False).returncode == 0:
                break
            time.sleep(.25)
        else:
            raise RuntimeError('Disposable PostgreSQL did not become ready.')
        info = json.loads(docker('inspect', container).stdout)[0]
        port = info['NetworkSettings']['Ports']['5432/tcp'][0]['HostPort']
        (directory / 'Program.cs').write_text((root / 'Zullo.Api.Tests/BackupRestoreCheck.csx').read_text())
        (directory / 'RestoreCheck.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.23" /><ProjectReference Include="{root / 'Zullo.Api.csproj'}" /></ItemGroup></Project>''')
        snapshot = directory / 'synthetic-snapshot.json'

        def fixture(mode, database):
            fixture_env = dict(env, ZULLO_BACKUP_DRILL_ID=drill_id, ZULLO_BACKUP_DRILL_CONNECTION=
                               f'Host=127.0.0.1;Port={port};Database={database};Username=zullo;Password={password}')
            result = run(['dotnet', 'run', '--project', str(directory / 'RestoreCheck.csproj'),
                          '--', mode, str(snapshot)], process_env=fixture_env)
            for line in result.stdout.splitlines():
                if line.startswith('PASS:'):
                    print(line, flush=True)

        fixture('seed', source_database)
        docker('exec', '--env', 'PGPASSWORD', container, 'pg_dump', '-U', 'zullo',
               f'--dbname={source_database}', '--format=custom', '--no-owner', '--no-acl', '--file=/tmp/synthetic.dump')
        archive = directory / 'synthetic.dump'
        docker('cp', f'{container}:/tmp/synthetic.dump', str(archive))
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        # pg_restore validates the archive contents; checksum alone is not a restore proof.
        docker('exec', '--env', 'PGPASSWORD', container, 'createdb', '-U', 'zullo', target_database)
        docker('exec', '--env', 'PGPASSWORD', container, 'pg_restore', '-U', 'zullo',
               f'--dbname={target_database}', '--exit-on-error', '--no-owner', '--no-acl', '/tmp/synthetic.dump')
        fixture('verify', target_database)
        # A checksum string alone must not make a broken archive acceptable.
        truncated = directory / 'truncated.dump'
        truncated.write_bytes(archive.read_bytes()[:128])
        docker('cp', str(truncated), f'{container}:/tmp/truncated.dump')
        rejected_database = 'zullo_restore_rejected_' + drill_id
        docker('exec', '--env', 'PGPASSWORD', container, 'createdb', '-U', 'zullo', rejected_database)
        rejected = docker('exec', '--env', 'PGPASSWORD', container, 'pg_restore', '-U', 'zullo',
                          f'--dbname={rejected_database}', '--exit-on-error', '--no-owner', '--no-acl',
                          '/tmp/truncated.dump', check=False)
        if rejected.returncode == 0:
            raise RuntimeError('A truncated archive was incorrectly accepted.')
        print('PASS: actual pg_restore rejects a truncated archive.', flush=True)
        print(f'PASS: real pg_dump/pg_restore completed in {time.monotonic() - started:.1f}s; '
              f'synthetic archive SHA256 {digest}. Only disposable test data was used.', flush=True)
finally:
    if created:
        docker('rm', '--force', '--volumes', container, check=False)
