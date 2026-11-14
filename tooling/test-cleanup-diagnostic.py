import importlib.util
from pathlib import Path
import signal
import json
from unittest.mock import MagicMock
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('custody', Path(__file__).with_name('contact-hosted-custody.py'))
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)

class CleanupDiagnosticControls(unittest.TestCase):
    def test_changed_executable_is_refused_and_only_numeric_context_is_recorded(self):
        old = {'pid': 6446, 'parentPid': 6424, 'session': 6193, 'startTicks': 21211, 'uid': 1001, 'executable': '/private/old'}
        new = dict(old, parentPid=1, executable='/private/new')
        contexts = []
        with patch.object(custody, 'process_metadata', return_value=new), patch.object(custody, 'selected_identity', return_value=new):
            alive, failures = custody.known_alive([old], contexts)
        self.assertEqual([], alive)
        self.assertEqual(['RuntimeError'], failures)
        self.assertEqual('observed-executable-changed', contexts[0]['censusGuard']['reason'])
        self.assertEqual('not-performed', contexts[0]['censusGuard']['ownershipDetermination'])
        self.assertNotIn('/private/', str(contexts))

    def test_signal_mismatch_is_refused_and_never_signalled(self):
        old = {'pid': 6446, 'parentPid': 6424, 'session': 6193, 'startTicks': 21211, 'uid': 1001, 'executable': '/private/old'}
        new = dict(old, executable='/private/new')
        with patch.object(custody, 'identity', return_value=new), patch.object(custody.os, 'kill') as kill:
            with self.assertRaises(RuntimeError) as captured:
                custody.signal_exact(old, signal.SIGTERM)
        kill.assert_not_called()
        context = custody.failure_context(captured.exception)
        self.assertEqual('signal-identity-changed', context['censusGuard']['reason'])
        self.assertNotIn('/private/', str(context))

    def test_repeated_mismatch_is_bounded_and_counts_dropped(self):
        old = {'pid': 6446, 'parentPid': 6424, 'session': 6193, 'startTicks': 21211, 'uid': 1001, 'executable': '/private/old'}
        new = dict(old, parentPid=1, executable='/private/new')
        receipt = {'cleanupFailures': []}
        with patch.object(custody, 'process_metadata', return_value=new), patch.object(custody, 'selected_identity', return_value=new):
            for _ in range(300):
                alive, failures = custody.known_alive([old], receipt)
                self.assertEqual(([], []), (alive, failures))
        self.assertTrue(receipt['cleanupHadFailure'])
        self.assertEqual(16, len(receipt['cleanupFailures']))
        self.assertEqual(284, receipt['cleanupFailuresDropped'])
        self.assertEqual(1, len(receipt['cleanupFailureContexts']))
        self.assertEqual(300, receipt['cleanupFailureContexts'][0]['occurrences'])
        self.assertNotIn('/private/', str(receipt))

    def test_distinct_context_overflow_is_explicitly_counted(self):
        receipt = {'cleanupFailures': []}
        for pid in range(1, 21):
            failure = custody.census_runtime_error('redacted', 'candidate', 'metadata-unavailable', metadata={'pid': pid})
            custody.record_cleanup_failure(receipt, failure)
        self.assertEqual(16, len(receipt['cleanupFailures']))
        self.assertEqual(4, receipt['cleanupFailuresDropped'])
        self.assertEqual(16, len(receipt['cleanupFailureContexts']))
        self.assertEqual(4, receipt['cleanupContextsDropped'])
        self.assertTrue(receipt['cleanupHadFailure'])

    def test_cleanup_error_recording_does_not_observe_procfs(self):
        receipt = {'cleanupFailures': []}
        with patch.object(custody, 'process_metadata', side_effect=AssertionError('no extra observation')) as probe:
            custody.record_cleanup_failure(receipt, PermissionError(13, 'private', '/proc/123/stat'))
        probe.assert_not_called()
        self.assertTrue(receipt['cleanupHadFailure'])
        self.assertEqual('stat', receipt['cleanupFailureContexts'][0]['procAccess'])
        self.assertNotIn('private', str(receipt))

    def test_recording_failure_does_not_skip_known_child_settlement(self):
        child = MagicMock(pid=17, returncode=0)
        child.poll.return_value = 0
        child.wait.return_value = 0
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        receipts = []
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=original), \
                patch.object(custody, 'members', side_effect=RuntimeError('synthetic census')), \
                patch.object(custody, 'known_alive', return_value=([], [])), \
                patch.object(custody, 'signal_exact') as stop, \
                patch.object(custody, 'failure_context', side_effect=RuntimeError('injected recording failure')), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: receipts.append(json.loads(text))):
            with self.assertRaises(RuntimeError):
                custody.execute('/sdk/dotnet', ['build', custody.SOLUTION], 'build', 3145728, {})
        stop.assert_called_once_with(original, signal.SIGTERM)
        child.wait.assert_any_call(timeout=5)
        containers.assert_not_called()
        self.assertTrue(receipts[0]['retainedChildJoined'])
        self.assertTrue(receipts[0]['cleanupDiagnosticRecordingFailed'])
        self.assertGreaterEqual(receipts[0]['cleanupContextsDropped'], 1)
        self.assertFalse(receipts[0]['cleanupVerified'])

if __name__ == '__main__':
    unittest.main()
