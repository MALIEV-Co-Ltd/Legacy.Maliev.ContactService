"""Control failure paths without starting an SDK, process, or container."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('custody', Path(__file__).with_name('contact-hosted-custody.py'))
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)


class CustodyControls(unittest.TestCase):
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
