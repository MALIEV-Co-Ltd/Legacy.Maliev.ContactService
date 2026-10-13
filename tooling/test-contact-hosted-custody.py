"""Control failure paths without starting an SDK, process, or container."""
import datetime as dt
import errno
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('custody', Path(__file__).with_name('contact-hosted-custody.py'))
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)


class CustodyControls(unittest.TestCase):
    def test_proc_access_diagnostics_are_categorical_and_numeric_only(self):
        for access in ('stat', 'environ', 'exe'):
            for uid in (0, 1001):
                with self.subTest(access=access, uid=uid):
                    primary = PermissionError(errno.EACCES, 'SENSITIVE synthetic message', '/proc/41/' + access)
                    metadata = {'pid': 41, 'session': 4, 'startTicks': 100, 'uid': uid, 'state': 'S', 'unapprovedField': 'SENSITIVE synthetic data'}
                    with patch.object(custody, 'process_metadata', return_value=metadata):
                        context = custody.failure_context(primary)
                    self.assertEqual(41, context['procPid'])
                    self.assertEqual(access, context['procAccess'])
                    self.assertEqual(errno.EACCES, context['errno'])
                    self.assertTrue(context['observedAfterFailure'])
                    self.assertEqual('not-performed', context['ownershipDetermination'])
                    self.assertNotIn('SENSITIVE', json.dumps(context))
                    self.assertEqual({'pid', 'session', 'startTicks', 'uid', 'state'}, set(context['observedMetadata']))

    def test_denied_metadata_keeps_primary_proc_access_without_reading_content(self):
        primary = PermissionError(errno.EACCES, 'SENSITIVE', '/proc/41/environ')
        with patch.object(custody, 'process_metadata', side_effect=PermissionError('synthetic metadata denial')), \
                patch.object(Path, 'read_bytes') as content:
            context = custody.failure_context(primary)
        self.assertEqual('environ', context['procAccess'])
        self.assertEqual('PermissionError', context['metadataUnavailable'])
        content.assert_not_called()

    def test_unknown_or_sensitive_filename_and_invalid_pid_are_not_retained(self):
        for filename in ('/private/customer-data', '/proc/0/environ', '/proc/2147483648/exe', '/proc/41/fd/3'):
            with self.subTest(filename=filename), patch.object(custody, 'process_metadata') as metadata:
                context = custody.failure_context(PermissionError(errno.EACCES, 'SENSITIVE', filename))
            self.assertEqual({'exceptionType': 'PermissionError'}, context)
            metadata.assert_not_called()

    def test_throwing_diagnostic_getter_is_guarded(self):
        class UnreadableDiagnostic(PermissionError):
            def __getattribute__(self, name):
                if name == 'filename':
                    raise RuntimeError('SENSITIVE synthetic getter')
                return super().__getattribute__(name)
        context = custody.failure_context(UnreadableDiagnostic())
        self.assertEqual('RuntimeError', context['contextUnavailable'])
        self.assertNotIn('SENSITIVE', json.dumps(context))

    def test_pid_birth_snapshot_is_observation_not_original_ownership(self):
        primary = PermissionError(errno.EACCES, 'synthetic primary', '/proc/41/environ')
        replacement = {'pid': 41, 'session': 41, 'startTicks': 999, 'uid': 1001, 'state': 'R'}
        with patch.object(custody, 'process_metadata', return_value=replacement):
            context = custody.failure_context(primary)
        self.assertEqual(999, context['observedMetadata']['startTicks'])
        self.assertTrue(context['observedAfterFailure'])
        self.assertEqual('not-performed', context['ownershipDetermination'])

    def test_diagnostic_primary_remains_exact_and_retained_child_is_joined(self):
        primary = PermissionError(errno.EACCES, 'SENSITIVE', '/proc/41/environ')
        with patch.object(custody, 'process_metadata', side_effect=PermissionError('synthetic metadata denial')):
            receipt = self.verify_census_failure_settles_known_owner(False, primary)
        self.assertEqual(41, receipt['failureContext']['procPid'])
        self.assertEqual('environ', receipt['failureContext']['procAccess'])
        self.assertEqual('PermissionError', receipt['failureContext']['metadataUnavailable'])
        self.assertTrue(receipt['retainedChildJoined'])
        self.assertEqual(0, receipt['actualChildExitCode'])
        self.assertFalse(receipt['cleanupVerified'])
        self.assertNotIn('SENSITIVE', json.dumps(receipt))

    def test_proc_diagnostic_and_receipt_recording_failure_do_not_mask_primary(self):
        primary = PermissionError(errno.EACCES, 'SENSITIVE', '/proc/41/environ')
        with patch.object(custody, 'process_metadata', side_effect=PermissionError('synthetic metadata denial')):
            observed = self.execute_with_recording_failure('cancel', primary)
        self.assertIs(primary, observed)

    def test_proc_directory_stat_diagnostic_is_categorical_and_numeric_only(self):
        primary = PermissionError(errno.EACCES, 'SENSITIVE', '/proc/41')
        metadata = {'pid': 41, 'parentPid': 1, 'session': 4, 'startTicks': 100,
                    'uid': 1001, 'state': 'S', 'unapprovedField': 'SENSITIVE'}
        with patch.object(custody, 'process_metadata', return_value=metadata):
            context = custody.failure_context(primary)
        self.assertEqual(41, context['procPid'])
        self.assertEqual('directory-stat', context['procAccess'])
        self.assertEqual(errno.EACCES, context['errno'])
        self.assertEqual('not-performed', context['ownershipDetermination'])
        self.assertTrue(context['observedAfterFailure'])
        self.assertEqual({key: metadata[key] for key in ('pid', 'parentPid', 'session', 'startTicks', 'uid', 'state')},
                         context['observedMetadata'])
        self.assertNotIn('SENSITIVE', json.dumps(context))
        for filename in ('/proc/41/', '/proc/41/status', '/proc/41/fd', '/proc/41/../42', '/proc/0', '/proc/2147483648'):
            with self.subTest(filename=filename), patch.object(custody, 'process_metadata') as probe:
                context = custody.failure_context(PermissionError(errno.EACCES, 'SENSITIVE', filename))
            self.assertEqual({'exceptionType': 'PermissionError'}, context)
            probe.assert_not_called()

    def test_directory_stat_failure_keeps_exact_primary_and_failed_census(self):
        primary = PermissionError(errno.EACCES, 'SENSITIVE', '/proc/41')
        with patch.object(custody, 'process_metadata', side_effect=primary):
            receipt = self.verify_census_failure_settles_known_owner(False, primary)
        self.assertEqual(41, receipt['failureContext']['procPid'])
        self.assertEqual('directory-stat', receipt['failureContext']['procAccess'])
        self.assertEqual(errno.EACCES, receipt['failureContext']['errno'])
        self.assertEqual('PermissionError', receipt['failureContext']['metadataUnavailable'])
        self.assertTrue(receipt['retainedChildJoined'])
        self.assertFalse(receipt['cleanupVerified'])
        self.assertNotIn('SENSITIVE', json.dumps(receipt))

    def test_diagnostic_numeric_caps_and_state_allowlist_reject_untyped_content(self):
        primary = PermissionError(70000, 'SENSITIVE', '/proc/41/stat')
        metadata = {'pid': 41, 'session': 'SENSITIVE', 'startTicks': 9223372036854775808, 'uid': True, 'state': 'SENSITIVE'}
        with patch.object(custody, 'process_metadata', return_value=metadata):
            context = custody.failure_context(primary)
        self.assertNotIn('errno', context)
        self.assertEqual({'pid': 41}, context['observedMetadata'])
        self.assertNotIn('SENSITIVE', json.dumps(context))

    def test_recycled_session_generation_never_promotes_foreign_rows(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        replacement = dict(original, startTicks=31, state='S')
        foreign = dict(replacement, pid=18, startTicks=32)
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/17'), Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', side_effect=lambda pid: replacement if pid == 17 else foreign), \
                patch.object(custody, 'identity') as probe, \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', return_value=b'UNRELATED=synthetic\0'), \
                patch.object(custody.os, 'kill') as kill:
            self.assertEqual([], custody.members(original, 'owned-lease', [original]))
        probe.assert_not_called()
        kill.assert_not_called()

    def test_sanitized_child_requires_original_live_leader_generation(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001, 'executable': '/sdk/dotnet'}
        child = dict(original, pid=18, startTicks=31, executable='/sdk/child')
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', side_effect=lambda pid: original if pid == 17 else child), \
                patch.object(custody, 'identity', side_effect=lambda pid: original if pid == 17 else child), \
                patch.object(Path, 'read_bytes') as environment:
            rows = custody.members(original, 'owned-lease', [original])
        self.assertEqual([18], [row['pid'] for row in rows])
        environment.assert_not_called()

    def test_unanchored_unmarked_same_session_is_preserved_as_uncertain(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        child = dict(original, pid=18, startTicks=31, state='S')
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', side_effect=lambda pid: None if pid == 17 else child), \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', return_value=b''), \
                patch.object(custody, 'identity') as probe:
            with self.assertRaisesRegex(RuntimeError, 'session generation unverified'):
                custody.members(original, 'owned-lease', [original])
        probe.assert_not_called()

    def test_replaced_known_owner_is_not_escalated(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        with patch.object(custody, 'process_metadata', return_value=dict(original, startTicks=31)), \
                patch.object(custody, 'identity') as probe:
            self.assertEqual(([], []), custody.known_alive([original]))
        probe.assert_not_called()

    def test_same_birth_membership_change_is_not_reported_as_owner_exit(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        with patch.object(custody, 'process_metadata', return_value=dict(original, session=18)), \
                patch.object(custody, 'identity') as probe:
            alive, failures = custody.known_alive([original])
        self.assertEqual([], alive)
        self.assertEqual(['RuntimeError'], failures)
        probe.assert_not_called()

    def test_census_failure_honors_fixed_grace_then_escalates_only_known_owner(self):
        child = MagicMock(pid=17)
        child.poll.return_value = None
        child.wait.return_value = 0
        primary = PermissionError('synthetic census failure')
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        receipts = []
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=original), \
                patch.object(custody, 'members', side_effect=primary), \
                patch.object(custody, 'known_alive', return_value=([original], [])), \
                patch.object(custody, 'signal_exact') as stop, \
                patch.object(custody.signal, 'SIGKILL', 9, create=True), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(custody.time, 'monotonic', side_effect=[0, 1, 2, 5, 11]) as clock, \
                patch.object(custody.time, 'sleep') as sleep, \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: receipts.append(json.loads(text))):
            with self.assertRaises(PermissionError) as observed:
                custody.execute('/sdk/dotnet', ['build', custody.SOLUTION], 'build', 3145728, {})
        self.assertIs(primary, observed.exception)
        self.assertEqual(5, clock.call_count)
        self.assertEqual(2, sleep.call_count)
        self.assertEqual([15, 9], [call.args[1] for call in stop.call_args_list])
        self.assertTrue(all(call.args[0] == original for call in stop.call_args_list))
        self.assertTrue(receipts[0]['retainedChildJoined'])
        self.assertFalse(receipts[0]['cleanupVerified'])
        containers.assert_not_called()

    def test_foreign_uid_executable_and_environment_are_not_probed(self):
        foreign = {'pid': 1, 'session': 1, 'startTicks': 1, 'state': 'S', 'uid': 0}
        owned = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001}
        def executable(pid):
            if pid == 1:
                raise PermissionError('synthetic foreign executable')
            return dict(owned, executable='/sdk/dotnet')
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/1'), Path('/proc/17')]), \
                patch.object(custody, 'process_metadata', side_effect=lambda pid: foreign if pid == 1 else owned), \
                patch.object(custody, 'identity', side_effect=executable) as probe, \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes') as environment:
            rows = custody.members(dict(owned, executable='/sdk/dotnet'), 'synthetic-lease', [owned])
        self.assertEqual([17], [row['pid'] for row in rows])
        probe.assert_called_once_with(17)
        environment.assert_not_called()

    def test_same_uid_foreign_process_without_nonce_is_not_executable_probed(self):
        foreign = {'pid': 1, 'session': 1, 'startTicks': 1, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/1')]), \
                patch.object(custody, 'process_metadata', return_value=foreign), \
                patch.object(custody, 'identity', side_effect=PermissionError('synthetic foreign executable')) as probe, \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', return_value=b'UNRELATED=synthetic\0'):
            self.assertEqual([], custody.members({'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}, 'synthetic-lease', []))
        probe.assert_not_called()

    def test_unreadable_owned_executable_remains_failure(self):
        owned = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/17')]), \
                patch.object(custody, 'process_metadata', return_value=owned), \
                patch.object(custody, 'identity', side_effect=PermissionError('synthetic owned executable')):
            with self.assertRaises(PermissionError):
                custody.members({'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}, 'synthetic-lease', [])

    def test_unreadable_same_uid_nonce_remains_uncertain_failure(self):
        uncertain = {'pid': 18, 'session': 18, 'startTicks': 31, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', return_value=uncertain), \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', side_effect=PermissionError('synthetic unavailable ownership')):
            with self.assertRaises(PermissionError):
                custody.members({'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}, 'synthetic-lease', [])

    def test_selection_race_refuses_changed_birth_before_executable_use(self):
        owned = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001}
        with patch.object(custody, 'identity', return_value=dict(owned, startTicks=31, executable='/sdk/dotnet')):
            with self.assertRaisesRegex(RuntimeError, 'ownership changed'):
                custody.selected_identity(owned)

    def verify_census_failure_settles_known_owner(self, sdk_completed, primary=None):
        child = MagicMock(pid=17)
        child.returncode = 0
        child.poll.return_value = 0 if sdk_completed else None
        child.wait.return_value = 0
        primary = primary if primary is not None else PermissionError('synthetic census failure')
        actual = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001, 'executable': '/sdk/dotnet'}
        receipts = []
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=actual), \
                patch.object(custody, 'members', side_effect=primary), \
                patch.object(custody, 'known_alive', return_value=([], [])), \
                patch.object(custody, 'signal_exact') as stop, \
                patch.object(custody.signal, 'SIGKILL', 9, create=True), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(custody.time, 'monotonic', return_value=0), \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: receipts.append(json.loads(text))):
            with self.assertRaises(RuntimeError if sdk_completed else PermissionError) as result:
                custody.execute('/sdk/dotnet', ['build', custody.SOLUTION], 'build', 3145728, {})
        if not sdk_completed:
            self.assertIs(primary, result.exception)
        child.wait.assert_any_call(timeout=5)
        self.assertEqual([15], [call.args[1] for call in stop.call_args_list])
        self.assertTrue(receipts[0]['retainedChildJoined'])
        self.assertFalse(receipts[0]['terminal'])
        self.assertFalse(receipts[0]['cleanupVerified'])
        containers.assert_not_called()
        return receipts[0]

    def test_census_primary_does_not_skip_known_owner_settlement(self):
        self.verify_census_failure_settles_known_owner(False)

    def test_cleanup_census_failure_still_joins_child_without_claiming_all_clean(self):
        self.verify_census_failure_settles_known_owner(True)

    def execute_with_recording_failure(self, mode, primary=None):
        child = MagicMock(pid=17)
        child.returncode = 17 if mode == 'sdk' else 0
        child.poll.return_value = child.returncode if mode in ('sdk', 'success') else None
        child.wait.return_value = child.returncode
        recording = OSError('synthetic receipt failure')
        actual = {'pid': 17, 'session': 17, 'startTicks': 30, 'executable': '/sdk/dotnet'}
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=actual), \
                patch.object(custody, 'members', return_value=[]), \
                patch.object(custody, 'known_alive', return_value=([], [])), \
                patch.object(custody, 'signal_exact'), \
                patch.object(custody.signal, 'SIGKILL', 9, create=True), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=recording), \
                patch.object(custody.time, 'monotonic', side_effect=[0, 301, 301] if mode == 'timeout' else [0, 1, 1]), \
                patch.object(custody.time, 'sleep', side_effect=primary if mode == 'cancel' else None), \
                patch.object(custody.subprocess, 'CalledProcessError', return_value=primary), \
                patch.dict(custody.__dict__, {'TimeoutError': lambda message: primary}):
            with self.assertRaises(type(recording if mode == 'success' else primary)) as result:
                custody.execute('/sdk/dotnet', ['build', custody.SOLUTION], 'build', 3145728, {})
        child.wait.assert_called()
        containers.assert_called_once()
        self.assertIs(recording if mode == 'success' else primary, result.exception)
        return result.exception

    def test_receipt_failure_preserves_actual_sdk_primary(self):
        primary = custody.subprocess.CalledProcessError(17, ['synthetic-sdk'])
        result = self.execute_with_recording_failure('sdk', primary)
        self.assertIn('Contact custody receipt recording failed: OSError', result.__notes__)

    def test_receipt_failure_preserves_phase_timeout_primary(self):
        primary = TimeoutError('synthetic deadline')
        self.execute_with_recording_failure('timeout', primary)

    def test_receipt_failure_preserves_cancellation_primary(self):
        primary = InterruptedError('synthetic cancellation')
        self.execute_with_recording_failure('cancel', primary)

    def test_receipt_failure_without_primary_fails_the_phase(self):
        self.execute_with_recording_failure('success')

    def test_throwing_secondary_attachment_cannot_replace_primary(self):
        class RejectsNote(InterruptedError):
            def add_note(self, note):
                raise RuntimeError('synthetic immutable diagnostics')
        self.execute_with_recording_failure('cancel', RejectsNote('synthetic cancellation'))

    def test_memory_refusal_starts_no_sdk_and_records_setup_failure(self):
        written = []
        with patch.object(custody, 'physical_kib', return_value=3145727), \
                patch.object(custody.subprocess, 'Popen') as start, \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: written.append(text)):
            with self.assertRaisesRegex(RuntimeError, 'admission failed'):
                custody.execute('/sdk/dotnet', ['build', custody.SOLUTION], 'build', 3145728, {})
        start.assert_not_called()
        receipt = json.loads(written[0])
        self.assertEqual([], receipt['ownedProcesses'])
        self.assertTrue(receipt['cleanupVerified'])
        self.assertEqual('RuntimeError', receipt['classification'])

    def test_reused_pid_is_preserved(self):
        original = {'pid': 12, 'session': 12, 'startTicks': 30, 'executable': '/sdk/dotnet'}
        observed = dict(original, startTicks=31)
        with patch.object(custody, 'identity', return_value=observed), patch.object(custody.os, 'kill') as kill:
            with self.assertRaisesRegex(RuntimeError, 'identity changed'):
                custody.signal_exact(original, 15)
        kill.assert_not_called()

    def test_bind_mount_is_preserved_without_stop_or_remove(self):
        container_id = 'a' * 64
        lease = 'b' * 32
        created = dt.datetime.now(dt.timezone.utc)
        actual = {'Id': container_id, 'Created': created.isoformat(),
                  'Config': {'Labels': {'codex.contact.run': lease, 'codex.contact.fixture': 'c' * 32},
                             'Image': 'postgres:18-alpine'},
                  'Mounts': [{'Type': 'bind'}]}
        responses = [(container_id + '\n').encode(), json.dumps([actual]).encode()]
        with patch.object(custody, 'docker', side_effect=responses) as docker:
            with self.assertRaisesRegex(RuntimeError, 'Persistent or unknown mounts'):
                custody.cleanup_containers(lease, created - dt.timedelta(seconds=1), {'containers': []})
        self.assertEqual(2, docker.call_count)

    def test_foreign_label_is_preserved(self):
        container_id = 'a' * 64
        created = dt.datetime.now(dt.timezone.utc)
        actual = {'Id': container_id, 'Created': created.isoformat(), 'Config': {'Labels': {'codex.contact.run': 'foreign'}}}
        with patch.object(custody, 'docker', side_effect=[(container_id + '\n').encode(), json.dumps([actual]).encode()]) as docker:
            with self.assertRaisesRegex(RuntimeError, 'ownership changed'):
                custody.cleanup_containers('owned', created - dt.timedelta(seconds=1), {'containers': []})
        self.assertEqual(2, docker.call_count)

    def test_focused_precedes_unchanged_full_and_has_no_coverage_collector(self):
        original = ['test', custody.SOLUTION, '--configuration', 'Release', '--no-build', '--no-restore']
        with patch.object(custody.sys, 'argv', ['controller', '/sdk/dotnet', *original]), \
                patch.object(custody.os, 'environ', {'VSTestCollect': 'XPlat Code Coverage'}), \
                patch.object(custody, 'execute') as execute:
            custody.main()
        self.assertEqual(['focused', 'full'], [row.args[2] for row in execute.call_args_list])
        self.assertNotIn('VSTestCollect', execute.call_args_list[0].args[4])
        self.assertEqual(original, execute.call_args_list[1].args[1])
        self.assertEqual('XPlat Code Coverage', execute.call_args_list[1].args[4]['VSTestCollect'])
        self.assertEqual(4194304, execute.call_args_list[1].args[3])

    def test_unknown_sdk_target_is_refused(self):
        with patch.object(custody.sys, 'argv', ['controller', '/sdk/dotnet', 'test', 'Other.slnx']), \
                patch.object(custody, 'execute') as execute:
            with self.assertRaisesRegex(RuntimeError, 'Unexpected SDK command'):
                custody.main()
        execute.assert_not_called()


if __name__ == '__main__':
    unittest.main()
