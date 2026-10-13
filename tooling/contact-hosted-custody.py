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


def process_metadata(pid):
    try:
        raw = Path(f'/proc/{pid}/stat').read_text()
        fields = raw[raw.rfind(')') + 2:].split()
        return {'pid': pid, 'parentPid': int(fields[1]), 'session': int(fields[3]), 'startTicks': int(fields[19]),
                'state': fields[0], 'uid': Path(f'/proc/{pid}').stat().st_uid}
    except (FileNotFoundError, ProcessLookupError):
        return None


def identity(pid):
    row = process_metadata(pid)
    if row is None:
        return None
    try:
        return dict(row, executable=os.readlink(f'/proc/{pid}/exe'))
    except (FileNotFoundError, ProcessLookupError):
        return None


def selected_identity(row):
    actual = identity(row['pid'])
    if actual is not None and any(actual[key] != row[key] for key in ('session', 'startTicks', 'uid')):
        raise census_runtime_error('Selected ownership changed before executable verification; preserve process',
                                   'selected-generation', 'generation-changed', metadata=actual)
    return actual


def same_generation(actual, original):
    return actual is not None and all(actual[key] == original[key] for key in ('pid', 'session', 'startTicks', 'uid'))


def live_original_leader(anchor):
    # Probe only cheap metadata until the original leader generation is proved.
    metadata = process_metadata(anchor['pid'])
    if not same_generation(metadata, anchor):
        return False
    actual = selected_identity(metadata)
    if not same_generation(actual, anchor):
        return False
    if actual['executable'] != anchor['executable']:
        raise census_runtime_error('Original leader executable changed; preserve uncertain owner',
                                   'original-leader', 'leader-executable-changed', metadata=actual)
    return True


def validate_generation_metadata(row, stage='candidate', allow_zero_session=False):
    bounds = {'pid': (1, 2147483647), 'session': (0 if allow_zero_session is True else 1, 2147483647),
              'startTicks': (0, 9223372036854775807), 'uid': (0, 4294967295)}
    if not isinstance(row, dict):
        raise census_runtime_error('Process generation metadata unavailable; preserve uncertainty',
                                   stage, 'metadata-unavailable')
    for key, (lower, upper) in bounds.items():
        value = row.get(key)
        if type(value) is not int or not lower <= value <= upper:
            invalid_kind = 'missing' if type(row) is dict and key not in row else 'type' if type(value) is not int else 'range'
            raise census_runtime_error('Process generation metadata invalid; preserve uncertainty',
                                       stage, 'metadata-invalid', key, invalid_kind, row)


def members(anchor, lease, retained):
    # This fixed original allocation birth uses /proc/<pid>/stat starttime in
    # the controller/child's same live Linux boot and PID namespace.
    validate_generation_metadata(anchor, 'anchor')
    original = next((old for old in retained if same_generation(anchor, old)), None)
    validate_generation_metadata(original, 'retained-anchor')
    if anchor['session'] != anchor['pid']:
        raise census_runtime_error('Original SDK allocation anchor unavailable; preserve uncertainty',
                                   'anchor', 'anchor-session-mismatch', metadata=anchor)
    rows = []
    for path in Path('/proc').iterdir():
        if not path.name.isdigit():
            continue
        pid = int(path.name)
        row = process_metadata(pid)
        if row is None:
            continue
        validate_generation_metadata(row, 'candidate', allow_zero_session=True)
        known = next((old for old in retained if same_generation(row, old)), None)
        if known is not None:
            validate_generation_metadata(row, 'candidate')
            actual = selected_identity(row)
            if actual is not None:
                rows.append(actual)
            continue
        # Retained generations are handled first. Unknown older generations
        # cannot descend from this phase, even when a numeric SID was recycled.
        # Recheck cheap metadata before exclusion; a reuse race stays uncertain.
        if row['startTicks'] < anchor['startTicks']:
            rechecked = process_metadata(pid)
            validate_generation_metadata(rechecked, 'candidate-recheck', allow_zero_session=True)
            if not same_generation(rechecked, row):
                raise census_runtime_error('Older candidate generation changed; preserve uncertainty',
                                           'candidate-recheck', 'generation-changed', metadata=rechecked)
            continue
        # Zero-session metadata can exclude only a rechecked older generation;
        # it never supplies retained, live-session or nonce ownership evidence.
        validate_generation_metadata(row, 'candidate')
        # Sanitized nested proof children can lack the nonce. Admit them only
        # while the original leader generation is live before and after selection.
        if row['session'] == anchor['session'] and live_original_leader(anchor):
            actual = selected_identity(row)
            if actual is not None and live_original_leader(anchor):
                rows.append(actual)
                continue
        if row['uid'] == os.getuid():
            try:
                environment = Path(f'/proc/{pid}/environ').read_bytes().split(b'\0')
            except (FileNotFoundError, ProcessLookupError):
                continue
            if ('CONTACT_HOSTED_RUN_LEASE=' + lease).encode() in environment:
                actual = selected_identity(row)
                if actual is not None:
                    rows.append(actual)
                continue
        if row['session'] == anchor['session']:
            current_leader = process_metadata(anchor['pid'])
            replaced = current_leader is not None and current_leader['startTicks'] != anchor['startTicks']
            if not (replaced and current_leader['session'] == anchor['session']):
                raise census_runtime_error('Original session generation unverified; preserve unmarked members',
                                           'session-generation', 'session-unverified', metadata=row)
        # Unknown/foreign rows never gain ownership from a recycled numeric SID.
    return rows


def known_alive(retained):
    alive = []
    failures = []
    for row in retained:
        try:
            metadata = process_metadata(row['pid'])
            if metadata is None or metadata['startTicks'] != row['startTicks']:
                continue
            if not same_generation(metadata, row):
                raise RuntimeError('Known birth still present but membership changed; preserve uncertain owner')
            actual = selected_identity(metadata)
            if actual is not None:
                if actual['executable'] != row['executable']:
                    raise RuntimeError('Known executable changed; preserve owner')
                alive.append(row)
        except BaseException as failure:
            failures.append(type(failure).__name__)
    return alive, failures


def census_guard_context(stage, reason, field=None, invalid_kind=None, metadata=None):
    stages = ('anchor', 'retained-anchor', 'candidate', 'candidate-recheck',
              'selected-generation', 'original-leader', 'session-generation')
    reasons = ('metadata-unavailable', 'metadata-invalid', 'anchor-session-mismatch',
               'generation-changed', 'leader-executable-changed', 'session-unverified')
    if type(stage) is not str or stage not in stages or type(reason) is not str or reason not in reasons:
        return None
    if field is not None and (type(field) is not str or field not in ('pid', 'session', 'startTicks', 'uid')):
        return None
    if invalid_kind is not None and (type(invalid_kind) is not str or invalid_kind not in ('missing', 'type', 'range')):
        return None
    context = {'stage': stage, 'reason': reason, 'capturedAtFailure': True,
               'ownershipDetermination': 'not-performed'}
    if field is not None:
        context['field'] = field
    if invalid_kind is not None:
        context['invalidKind'] = invalid_kind
    if type(metadata) is dict:
        context['numericMetadata'] = {key: metadata[key] for key in ('pid', 'parentPid', 'session', 'startTicks', 'uid')
                                      if type(metadata.get(key)) is int and 0 <= metadata[key] <= 9223372036854775807}
    return context


def attach_census_guard_context(failure, stage, reason, field=None, invalid_kind=None, metadata=None):
    try:
        if type(failure) is RuntimeError:
            context = census_guard_context(stage, reason, field, invalid_kind, metadata)
            if context is not None:
                failure._contact_census_guard = context
    except BaseException:
        pass
    return failure


def census_runtime_error(message, stage, reason, field=None, invalid_kind=None, metadata=None):
    return attach_census_guard_context(RuntimeError(message), stage, reason, field, invalid_kind, metadata)


def failure_context(failure):
    """Retain only typed procfs access context, never messages or environment bytes."""
    context = {'exceptionType': type(failure).__name__}
    try:
        if type(failure) is RuntimeError:
            record = vars(failure).get('_contact_census_guard')
            if type(record) is dict:
                guard = census_guard_context(record.get('stage'), record.get('reason'), record.get('field'),
                                             record.get('invalidKind'), record.get('numericMetadata'))
                if guard is not None:
                    context['censusGuard'] = guard
            return context
        if not isinstance(failure, OSError):
            return context
        filename = failure.filename
        match = re.fullmatch(r'/proc/([0-9]+)(?:/(stat|environ|exe))?', filename) if isinstance(filename, str) else None
        if match is None:
            return context
        pid = int(match.group(1))
        if not 0 < pid <= 2147483647:
            return context
        context.update(procPid=pid, procAccess=match.group(2) or 'directory-stat', observedAfterFailure=True,
                       ownershipDetermination='not-performed')
        number = failure.errno
        if type(number) is int and 0 <= number <= 65535:
            context['errno'] = number
        try:
            metadata = process_metadata(pid)
            if metadata is not None:
                numeric = {key: metadata[key] for key in ('pid', 'parentPid', 'session', 'startTicks', 'uid')
                           if type(metadata.get(key)) is int and 0 <= metadata[key] <= 9223372036854775807}
                state = metadata.get('state')
                if type(state) is str and state in ('R', 'S', 'D', 'Z', 'T', 't', 'X', 'x', 'K', 'W', 'P', 'I', 'N'):
                    numeric['state'] = state
                context['observedMetadata'] = numeric
        except BaseException as observation:
            context['metadataUnavailable'] = type(observation).__name__
    except BaseException as recording:
        context['contextUnavailable'] = type(recording).__name__
    return context


def signal_exact(row, number):
    observed = identity(row['pid'])
    if observed is None:
        return
    if any(observed[key] != row[key] for key in ('session', 'startTicks', 'executable', 'uid')):
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
            for row in members(actual, lease, receipt['ownedProcesses']):
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
        receipt['failureContext'] = failure_context(failure)
    finally:
        if child is None:
            receipt['terminal'] = True
        if child is not None:
            receipt['terminal'] = False
            census_failed = False
            current = []
            try:
                current = members(receipt['ownedProcesses'][0], lease, receipt['ownedProcesses'])
            except BaseException as census:
                census_failed = True
                receipt['cleanupFailures'].append(type(census).__name__)
                receipt.setdefault('cleanupFailureContexts', []).append(failure_context(census))
            for row in current:
                if not any(old['pid'] == row['pid'] and old['startTicks'] == row['startTicks'] for old in receipt['ownedProcesses']):
                    receipt['ownedProcesses'].append(row)
            # Census failure does not discard retained, independently verified
            # process identities. Settle each known owner; do not claim unknowns.
            for row in receipt['ownedProcesses']:
                try:
                    signal_exact(row, signal.SIGTERM)
                except BaseException as cleanup:
                    receipt['cleanupFailures'].append(type(cleanup).__name__)
            try:
                grace = time.monotonic() + 10
                # One absolute grace deadline; census failure cannot skip it or
                # renew it. Observe retained original identities independently.
                while True:
                    child.poll()
                    if not census_failed:
                        try:
                            for row in members(receipt['ownedProcesses'][0], lease, receipt['ownedProcesses']):
                                if not any(old['pid'] == row['pid'] and old['startTicks'] == row['startTicks'] for old in receipt['ownedProcesses']):
                                    receipt['ownedProcesses'].append(row)
                                    signal_exact(row, signal.SIGTERM)
                        except BaseException as census:
                            census_failed = True
                            receipt['cleanupFailures'].append(type(census).__name__)
                            receipt.setdefault('cleanupFailureContexts', []).append(failure_context(census))
                    survivors, observation_failures = known_alive(receipt['ownedProcesses'])
                    receipt['cleanupFailures'].extend(observation_failures)
                    if not survivors or time.monotonic() >= grace:
                        break
                    time.sleep(0.1)
            except BaseException as census:
                census_failed = True
                receipt['cleanupFailures'].append(type(census).__name__)
                receipt.setdefault('cleanupFailureContexts', []).append(failure_context(census))
            survivors, observation_failures = known_alive(receipt['ownedProcesses'])
            receipt['cleanupFailures'].extend(observation_failures)
            for row in survivors:
                try:
                    signal_exact(row, signal.SIGKILL)
                except BaseException as cleanup:
                    receipt['cleanupFailures'].append(type(cleanup).__name__)
            # Joining the retained child is independent of census and signal failures.
            try:
                child.wait(timeout=5)
                receipt['retainedChildJoined'] = True
                actual_exit = child.returncode
                receipt['actualChildExitCode'] = actual_exit if type(actual_exit) is int and -2147483648 <= actual_exit <= 2147483647 else None
            except BaseException as cleanup:
                receipt['cleanupFailures'].append(type(cleanup).__name__)
            try:
                if census_failed:
                    raise RuntimeError('Known child joined, complete census unverified; preserve unknown resources')
                remaining = members(receipt['ownedProcesses'][0], lease, receipt['ownedProcesses'])
                receipt['remainingOwnedProcesses'] = remaining
                receipt['terminal'] = receipt.get('retainedChildJoined', False) and not remaining
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
