"""Own existing Contact validation commands; preserve the shared action and full suite."""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid

SOLUTION = 'Legacy.Maliev.ContactService.slnx'
FOCUS = 'FullyQualifiedName~ContactSourcePaginationCardinalityHttpTests|FullyQualifiedName~ContactFixtureLifecycleTests'
SOURCE_PATHS = ('.github/workflows/_build-and-test.yml',
                'Legacy.Maliev.ContactService.Tests/Controllers/ContactRuntimeParityBoundaryTests.cs',
                'Legacy.Maliev.ContactService.Tests/Controllers/ContactFixtureLifecycle.cs',
                'Legacy.Maliev.ContactService.Tests/Controllers/ContactFixtureLifecycleTests.cs',
                'Legacy.Maliev.ContactService.Tests/Controllers/ContactSourcePaginationCardinalityHttpTests.cs',
                'tooling/contact-hosted-custody.py', 'tooling/contact-hosted-dotnet.sh',
                'tooling/test-contact-hosted-custody.py')


def git(arguments):
    result = subprocess.run(['git', *arguments], capture_output=True, check=True, timeout=15)
    if len(result.stdout) > 262144:
        raise RuntimeError('Source evidence quota')
    return result.stdout


def source_pins():
    head = git(['rev-parse', 'HEAD']).decode().strip()
    if head != os.environ.get('GITHUB_SHA'):
        raise RuntimeError('Hosted checkout differs from exact tested commit')
    rows = []
    for path in SOURCE_PATHS:
        raw = Path(path).read_bytes()
        if raw != git(['show', 'HEAD:' + path]):
            raise RuntimeError('Tracked source drift before SDK admission')
        rows.append({'path': path, 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()})
    for directory, expected in (('Legacy.Maliev.ServiceDefaults', 'c40a7f82cea347b949444dcd7fb730f2b8dc3c0e'),
                                ('Legacy.Maliev.CompatibilityContracts', '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7')):
        if git(['-C', '.dependencies/' + directory, 'rev-parse', 'HEAD']).decode().strip() != expected:
            raise RuntimeError('Dependency exact pin changed')
        if git(['-C', '.dependencies/' + directory, 'status', '--porcelain', '--untracked-files=all']).strip():
            raise RuntimeError('Dirty pinned dependency preserved')
    return {'testedCommit': head, 'files': rows}


def cancel(signum, frame):
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    raise InterruptedError('Owned validation canceled; settling resources')


def physical_kib():
    rows = Path('/proc/meminfo').read_text().splitlines()
    return int(next(row for row in rows if row.startswith('MemAvailable:')).split()[1])


def identity(pid):
    try:
        raw = Path(f'/proc/{pid}/stat').read_text()
        fields = raw[raw.rfind(')') + 2:].split()
        return {'pid': pid, 'session': int(fields[3]), 'startTicks': int(fields[19]),
                'state': fields[0], 'executable': os.readlink(f'/proc/{pid}/exe')}
    except (FileNotFoundError, ProcessLookupError):
        return None


def members(session, lease):
    rows = []
    for path in Path('/proc').iterdir():
        if not path.name.isdigit():
            continue
        pid = int(path.name)
        row = identity(pid)
        if row is None:
            continue
        # POSIX sessions cannot be joined by an unrelated process. The retained
        # Popen created this private session. Existing nested proof helpers may
        # sanitize inherited environment, while owned setsid helpers retain the
        # exact per-command nonce even when they create their own session.
        if row['session'] == session:
            rows.append(row)
            continue
        try:
            environment = Path(f'/proc/{pid}/environ').read_bytes().split(b'\0')
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        if ('CONTACT_HOSTED_RUN_LEASE=' + lease).encode() in environment:
            rows.append(row)
    return rows


def signal_exact(row, number):
    observed = identity(row['pid'])
    if observed is None:
        return
    if any(observed[key] != row[key] for key in ('session', 'startTicks', 'executable')):
        raise RuntimeError('Process identity changed; preserve it')
    os.kill(row['pid'], number)


def docker(arguments):
    result = subprocess.run(['docker', *arguments], capture_output=True, timeout=15, check=True)
    if len(result.stdout) > 1048576:
        raise RuntimeError('Docker evidence quota')
    return result.stdout


def cleanup_containers(lease, started, receipt):
    ids = docker(['ps', '-aq', '--no-trunc', '--filter', 'label=codex.contact.run=' + lease]).decode().splitlines()
    for container_id in ids:
        if re.fullmatch('[0-9a-f]{64}', container_id) is None:
            raise RuntimeError('Invalid owned container ID')
        actual = json.loads(docker(['inspect', container_id]))[0]
        labels = actual['Config']['Labels']
        created = dt.datetime.fromisoformat(actual['Created'].replace('Z', '+00:00'))
        if actual['Id'] != container_id or labels.get('codex.contact.run') != lease or created < started:
            raise RuntimeError('Container ownership changed; preserve it')
        if re.fullmatch('[0-9a-f]{32}', labels.get('codex.contact.fixture', '')) is None:
            raise RuntimeError('Unknown fixture ownership; preserve it')
        if actual['Config']['Image'] != 'postgres:18-alpine' or any(mount['Type'] != 'tmpfs' for mount in actual['Mounts']):
            raise RuntimeError('Persistent or unknown mounts; preserve container')
        receipt['containers'].append({'id': container_id, 'created': actual['Created'],
                                      'fixture': labels['codex.contact.fixture'], 'mounts': actual['Mounts'],
                                      'persistentData': False})
        docker(['stop', '--time', '10', container_id])
        docker(['rm', container_id])
        remaining = docker(['ps', '-aq', '--no-trunc', '--filter', 'id=' + container_id]).decode().strip()
        if remaining:
            raise RuntimeError('Owned container remains')
    if docker(['ps', '-aq', '--no-trunc', '--filter', 'label=codex.contact.run=' + lease]).strip():
        raise RuntimeError('Owned fixture container remains')


def execute(executable, arguments, phase, floor, environment):
    lease = uuid.uuid4().hex
    started = dt.datetime.now(dt.timezone.utc)
    receipt = {'phase': phase, 'lease': lease, 'startedUtc': started.isoformat(),
               'expiryUtc': (started + dt.timedelta(seconds=660)).isoformat(),
               'floorKiB': floor, 'availableKiB': physical_kib(), 'ownedProcesses': [],
               'containers': [], 'cleanupFailures': [], 'exitCode': None, 'terminal': False,
               'classification': None}
    directory = Path('runner-results/hosted-custody')
    directory.mkdir(parents=True, exist_ok=True)
    child = None
    primary = None
    try:
        if receipt['availableKiB'] < floor:
            raise RuntimeError('Physical memory admission failed before SDK; setup failure, not behavioral RED')
        receipt['codePins'] = source_pins()
        receipt['availableKiB'] = physical_kib()
        if receipt['availableKiB'] < floor:
            raise RuntimeError('Fresh physical memory admission failed before SDK; setup failure, not behavioral RED')
        environment = dict(environment, CONTACT_HOSTED_RUN_LEASE=lease,
                           CONTACT_FIXTURE_CUSTODY_DIRECTORY=str(Path('runner-results/fixture-custody').resolve()),
                           DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1', MSBUILDDISABLENODEREUSE='1')
        child = subprocess.Popen([executable, *arguments], env=environment, start_new_session=True)
        actual = identity(child.pid)
        if actual is None or actual['session'] != child.pid:
            raise RuntimeError('Actual SDK session identity unavailable')
        receipt['ownedProcesses'].append(actual)
        deadline = time.monotonic() + (600 if phase == 'full' else 300)
        while child.poll() is None:
            for row in members(child.pid, lease):
                if not any(old['pid'] == row['pid'] and old['startTicks'] == row['startTicks'] for old in receipt['ownedProcesses']):
                    receipt['ownedProcesses'].append(row)
            if time.monotonic() >= deadline:
                raise TimeoutError('Owned SDK phase deadline; setup/timeout, not behavioral RED')
            time.sleep(0.1)
        receipt['exitCode'] = child.wait()
        receipt['terminal'] = True
        if child.returncode:
            raise subprocess.CalledProcessError(child.returncode, [executable, *arguments])
    except BaseException as failure:
        primary = failure
        receipt['classification'] = type(failure).__name__
    finally:
        if child is None:
            receipt['terminal'] = True
        if child is not None:
            receipt['terminal'] = False
            try:
                current = members(child.pid, lease)
                for row in current:
                    if not any(old['pid'] == row['pid'] and old['startTicks'] == row['startTicks'] for old in receipt['ownedProcesses']):
                        receipt['ownedProcesses'].append(row)
                    signal_exact(row, signal.SIGTERM)
                grace = time.monotonic() + 10
                while members(child.pid, lease) and time.monotonic() < grace:
                    child.poll()
                    time.sleep(0.1)
                for row in members(child.pid, lease):
                    signal_exact(row, signal.SIGKILL)
                child.wait(timeout=5)
                remaining = members(child.pid, lease)
                receipt['remainingOwnedProcesses'] = remaining
                receipt['terminal'] = not remaining
                if remaining:
                    raise RuntimeError('Owned processes remain')
            except BaseException as cleanup:
                receipt['cleanupFailures'].append(type(cleanup).__name__)
            if receipt['terminal']:
                try:
                    cleanup_containers(lease, started, receipt)
                except BaseException as cleanup:
                    receipt['cleanupFailures'].append(type(cleanup).__name__)
        receipt['cleanupVerified'] = receipt['terminal'] and not receipt['cleanupFailures']
        receipt['endedUtc'] = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            (directory / (phase + '-' + lease + '.json')).write_text(json.dumps(receipt, indent=2) + '\n')
        except BaseException as recording:
            if primary is None:
                # A successful SDK phase without its custody receipt is not accepted.
                primary = recording
            else:
                try:
                    primary.add_note('Contact custody receipt recording failed: ' + type(recording).__name__)
                except BaseException:
                    # Diagnostic attachment must not replace SDK/timeout/cancel failure.
                    pass
    if primary is not None:
        raise primary
    if not receipt['cleanupVerified']:
        raise RuntimeError('Owned SDK/container cleanup unverified')


def main():
    executable, *arguments = sys.argv[1:]
    environment = dict(os.environ)
    if len(arguments) < 2 or arguments[1] != SOLUTION or arguments[0] not in ('restore', 'build', 'test', 'format', 'list'):
        raise RuntimeError('Unexpected SDK command refused')
    phase = arguments[0]
    if phase == 'test':
        focused = dict(environment)
        focused.pop('VSTestCollect', None)
        focused['VSTestResultsDirectory'] = str(Path('focused-results').resolve())
        execute(executable, [*arguments, '--filter', FOCUS], 'focused', 4194304, focused)
        execute(executable, arguments, 'full', 4194304, environment)
    else:
        if phase == 'build':
            arguments.append('-warnaserror')
        execute(executable, arguments, phase, 3145728, environment)


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, cancel)
    signal.signal(signal.SIGINT, cancel)
    try:
        main()
    except BaseException as failure:
        try:
            print('Contact hosted validation failed: ' + type(failure).__name__, file=sys.stderr)
        except BaseException:
            pass
        sys.exit(2)
