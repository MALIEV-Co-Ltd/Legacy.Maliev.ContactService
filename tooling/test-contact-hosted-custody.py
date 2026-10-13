"""Control failure paths without starting an SDK, process, or container."""
import datetime as dt
import errno
import importlib.util
import json
from contextlib import ExitStack
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('custody', Path(__file__).with_name('contact-hosted-custody.py'))
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)


class CustodyControls(unittest.TestCase):
    def candidate_zero_controls(self, candidate, environment=b'UNRELATED=synthetic\0', recheck=...,
                                environment_failure=None, nonce=False, retained=False):
        anchor = {'pid': 6512, 'session': 6512, 'startTicks': 11505, 'uid': 1001, 'executable': '/sdk/dotnet'}
        observed = dict(candidate) if recheck is ... else recheck
        primary = environment_failure if environment_failure is not None else observed if isinstance(observed, BaseException) else None
        with ExitStack() as stack:
            stack.enter_context(patch.object(Path, 'iterdir', return_value=[Path('/proc/' + str(candidate['pid']))]))
            metadata = stack.enter_context(patch.object(custody, 'process_metadata', side_effect=[candidate, observed]))
            stack.enter_context(patch.object(custody.os, 'getuid', return_value=1001, create=True))
            content = b'CONTACT_HOSTED_RUN_LEASE=owned-lease\0' if nonce else environment
            environ = stack.enter_context(patch.object(Path, 'read_bytes', side_effect=environment_failure,
                                                       return_value=content))
            forbidden = [stack.enter_context(patch.object(custody, name,
                side_effect=AssertionError('Ownership getter forbidden')))
                for name in ('identity', 'selected_identity', 'live_original_leader', 'signal_exact')]
            forbidden.append(stack.enter_context(patch.object(custody.os, 'kill',
                side_effect=AssertionError('Signal forbidden'))))
            known = [anchor, dict(candidate, executable='/sdk/retained')] if retained else [anchor]
            if primary is not None:
                with self.assertRaises(type(primary)) as result:
                    custody.members(anchor, 'owned-lease', known)
                self.assertIs(primary, result.exception)
            elif nonce or retained or recheck is not ...:
                with self.assertRaises(RuntimeError):
                    custody.members(anchor, 'owned-lease', known)
            else:
                self.assertEqual([], custody.members(anchor, 'owned-lease', known))
            expected_reads = 1 if nonce or retained or environment_failure is not None else 2
            self.assertEqual([candidate['pid']] * expected_reads,
                             [call.args[0] for call in metadata.call_args_list])
            if candidate['uid'] == 1001 and not retained:
                environ.assert_called_once_with()
            else:
                environ.assert_not_called()
            for probe in forbidden:
                probe.assert_not_called()

    def test_actual_newer_6884_zero_session_shape_is_rechecked_and_preserved(self):
        self.candidate_zero_controls({'pid': 6884, 'parentPid': 2, 'session': 0,
                                      'startTicks': 12241, 'uid': 0})

    def test_zero_session_foreign_numeric_ids_have_no_special_exception(self):
        for pid, parent, uid in ((6884, 2, 0), (8001, 8000, 42), (9911, 9, 2001)):
            with self.subTest(pid=pid, uid=uid):
                self.candidate_zero_controls({'pid': pid, 'parentPid': parent, 'session': 0,
                                              'startTicks': 12241, 'uid': uid})

    def test_equal_newer_current_uid_zero_without_nonce_is_rechecked(self):
        for birth in (11505, 12241):
            with self.subTest(birth=birth):
                self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': birth, 'uid': 1001})

    def test_zero_session_actual_nonce_rejects_before_executable_or_signals(self):
        self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': 12241, 'uid': 1001}, nonce=True)

    def test_zero_session_retained_newer_owner_still_rejects_before_getters(self):
        self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': 12241, 'uid': 1001}, retained=True)

    def test_equal_newer_zero_current_uid_permission_failure_remains_primary(self):
        for birth in (11505, 12241):
            with self.subTest(birth=birth):
                self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': birth, 'uid': 1001},
                    environment_failure=PermissionError(errno.EACCES, 'synthetic', '/proc/6884/environ'))

    def test_current_uid_zero_unavailable_nonce_cannot_supply_exclusion(self):
        for primary in (FileNotFoundError('synthetic'), ProcessLookupError('synthetic')):
            with self.subTest(primary=type(primary).__name__):
                self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': 12241, 'uid': 1001},
                    environment_failure=primary)

    def test_newer_zero_recheck_changed_missing_invalid_remains_fail_closed(self):
        candidate = {'pid': 6884, 'parentPid': 2, 'session': 0, 'startTicks': 12241, 'uid': 0}
        for recheck in (None, dict(candidate, pid=6885), dict(candidate, session=6512),
                        dict(candidate, startTicks=12242), dict(candidate, uid=1001),
                        dict(candidate, session=True), dict(candidate, session=-1),
                        dict(candidate, session=2147483648)):
            with self.subTest(recheck=recheck):
                self.candidate_zero_controls(candidate, recheck=recheck)

    def test_newer_zero_recheck_permission_failure_preserves_primary(self):
        self.candidate_zero_controls({'pid': 6884, 'session': 0, 'startTicks': 12241, 'uid': 0},
            recheck=PermissionError(errno.EACCES, 'synthetic', '/proc/6884/stat'))

    def test_current_uid_zero_no_nonce_changed_recheck_is_not_exclusion(self):
        candidate = {'pid': 6884, 'session': 0, 'startTicks': 12241, 'uid': 1001}
        self.candidate_zero_controls(candidate, recheck=dict(candidate, startTicks=12242))

    def zero_session_candidate_controls(self, birth=33, recheck=..., retained=False):
        anchor = {'pid': 5710, 'session': 5710, 'startTicks': 6376, 'uid': 1001, 'executable': '/sdk/dotnet'}
        candidate = {'pid': 2, 'parentPid': 0, 'session': 0, 'startTicks': birth, 'uid': 0}
        effects = [candidate, dict(candidate) if recheck is ... else recheck]
        known = [anchor, dict(candidate, executable='/sdk/retained')] if retained else [anchor]
        with ExitStack() as stack:
            stack.enter_context(patch.object(Path, 'iterdir', return_value=[Path('/proc/2')]))
            metadata = stack.enter_context(patch.object(custody, 'process_metadata', side_effect=effects))
            stack.enter_context(patch.object(custody.os, 'getuid', return_value=1001, create=True))
            forbidden = [stack.enter_context(patch.object(custody, name, side_effect=AssertionError('Foreign getter forbidden')))
                         for name in ('identity', 'selected_identity', 'live_original_leader', 'signal_exact')]
            forbidden.append(stack.enter_context(patch.object(Path, 'read_bytes', side_effect=AssertionError('Foreign environ forbidden'))))
            forbidden.append(stack.enter_context(patch.object(custody.os, 'kill', side_effect=AssertionError('Foreign signal forbidden'))))
            expected_failure = retained or recheck is not ...
            if expected_failure:
                error_type = type(recheck) if isinstance(recheck, BaseException) else RuntimeError
                with self.assertRaises(error_type) as result:
                    custody.members(anchor, 'owned-lease', known)
                if isinstance(recheck, BaseException):
                    self.assertIs(recheck, result.exception)
            else:
                self.assertEqual([], custody.members(anchor, 'owned-lease', known))
            expected_reads = [2] if retained else [2, 2]
            self.assertEqual(expected_reads, [call.args[0] for call in metadata.call_args_list])
            for probe in forbidden:
                probe.assert_not_called()

    def test_actual_zero_session_older_shape_is_rechecked_without_ownership_or_foreign_getters(self):
        self.zero_session_candidate_controls()

    def test_equal_and_newer_zero_session_are_rechecked_without_ownership_getters(self):
        for birth in (6376, 6377):
            with self.subTest(birth=birth):
                self.zero_session_candidate_controls(birth=birth)

    def test_retained_zero_session_fails_before_selection_even_when_older(self):
        self.zero_session_candidate_controls(retained=True)

    def test_zero_session_anchor_and_retained_anchor_never_start_census(self):
        anchor = {'pid': 5710, 'session': 0, 'startTicks': 6376, 'uid': 1001, 'executable': '/sdk/dotnet'}
        with patch.object(Path, 'iterdir') as listing:
            with self.assertRaises(RuntimeError):
                custody.members(anchor, 'owned-lease', [anchor])
            listing.assert_not_called()
        with self.assertRaises(RuntimeError):
            custody.validate_generation_metadata(anchor, 'retained-anchor')

    def test_zero_session_recheck_changed_missing_and_invalid_remain_uncertain(self):
        candidate = {'pid': 2, 'parentPid': 0, 'session': 0, 'startTicks': 33, 'uid': 0}
        for recheck in (None, dict(candidate, pid=3), dict(candidate, session=1),
                        dict(candidate, startTicks=34), dict(candidate, uid=1001),
                        dict(candidate, session=-1), dict(candidate, startTicks=True)):
            with self.subTest(recheck=recheck):
                self.zero_session_candidate_controls(recheck=recheck)

    def test_zero_session_unreadable_recheck_preserves_exact_primary(self):
        primary = PermissionError(errno.EACCES, 'synthetic', '/proc/2/stat')
        self.zero_session_candidate_controls(recheck=primary)

    def test_candidate_zero_permission_keeps_all_session_type_and_range_guards(self):
        candidate = {'pid': 2, 'session': 0, 'startTicks': 33, 'uid': 0}
        custody.validate_generation_metadata(candidate, 'candidate', allow_zero_session=True)
        for value in (-1, True, '0', None, 2147483648):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                custody.validate_generation_metadata(dict(candidate, session=value), 'candidate', allow_zero_session=True)
        for field, value in (('pid', 0), ('pid', True), ('startTicks', -1), ('uid', -1)):
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                custody.validate_generation_metadata(dict(candidate, **{field: value}), 'candidate', allow_zero_session=True)

    def test_only_exact_true_flag_admits_zero_metadata_default_stays_positive(self):
        candidate = {'pid': 2, 'session': 0, 'startTicks': 33, 'uid': 0}
        with self.assertRaises(RuntimeError):
            custody.validate_generation_metadata(candidate)
        for flag in (False, 1, 'true', None):
            with self.subTest(flag=flag), self.assertRaises(RuntimeError):
                custody.validate_generation_metadata(candidate, allow_zero_session=flag)

    def test_census_invalid_metadata_has_constant_stage_field_reason_and_numeric_context(self):
        valid = {'pid': 18, 'session': 18, 'startTicks': 31, 'uid': 1001}
        for field, value, kind in (('session', 0, 'range'), ('uid', True, 'type'),
                                   ('startTicks', 9223372036854775808, 'range'), ('pid', 'SENSITIVE', 'type')):
            with self.subTest(field=field):
                with self.assertRaises(RuntimeError) as result:
                    custody.validate_generation_metadata(dict(valid, **{field: value}), 'candidate-recheck')
                self.assertIs(type(result.exception), RuntimeError)
                context = custody.failure_context(result.exception)['censusGuard']
                self.assertEqual('candidate-recheck', context['stage'])
                self.assertEqual('metadata-invalid', context['reason'])
                self.assertEqual(field, context['field'])
                self.assertEqual(kind, context['invalidKind'])
                self.assertTrue(context['capturedAtFailure'])
                self.assertEqual('not-performed', context['ownershipDetermination'])
                self.assertNotIn('SENSITIVE', json.dumps(context))

    def test_census_unavailable_and_missing_metadata_keep_runtime_error_guard(self):
        for metadata in (None, {'pid': 17, 'session': 17, 'uid': 1001}):
            with self.subTest(metadata=metadata):
                with self.assertRaises(RuntimeError) as result:
                    custody.validate_generation_metadata(metadata, 'retained-anchor')
                context = custody.failure_context(result.exception)['censusGuard']
                self.assertEqual('retained-anchor', context['stage'])
                if metadata is None:
                    self.assertEqual('metadata-unavailable', context['reason'])
                else:
                    self.assertEqual('startTicks', context['field'])
                    self.assertEqual('missing', context['invalidKind'])

    def test_census_context_excludes_unapproved_content_and_caps_exact_numeric_types(self):
        metadata = {'pid': 18, 'parentPid': 1, 'session': 0, 'startTicks': 9223372036854775808,
                    'uid': True, 'state': 'SENSITIVE', 'argv': 'SENSITIVE', 'unapprovedField': 'SENSITIVE'}
        primary = custody.census_runtime_error('SENSITIVE', 'candidate', 'metadata-invalid', 'session', 'range', metadata)
        context = custody.failure_context(primary)
        self.assertEqual({'pid': 18, 'parentPid': 1, 'session': 0}, context['censusGuard']['numericMetadata'])
        self.assertNotIn('SENSITIVE', json.dumps(context))
        self.assertNotIn('state', context['censusGuard']['numericMetadata'])

    def test_census_diagnostic_attachment_failure_preserves_exact_primary(self):
        primary = RuntimeError('SENSITIVE original')
        with patch.object(custody, 'census_guard_context', side_effect=OSError('SENSITIVE recording')):
            observed = custody.attach_census_guard_context(primary, 'candidate', 'metadata-invalid')
        self.assertIs(primary, observed)
        self.assertEqual({'exceptionType': 'RuntimeError'}, custody.failure_context(primary))

    def test_census_diagnostic_copy_failure_does_not_replace_or_expose_primary(self):
        primary = custody.census_runtime_error('SENSITIVE original', 'candidate', 'metadata-invalid')
        with patch.object(custody, 'census_guard_context', side_effect=PermissionError('SENSITIVE recording')):
            context = custody.failure_context(primary)
        self.assertEqual({'exceptionType': 'RuntimeError', 'contextUnavailable': 'PermissionError'}, context)
        with self.assertRaises(RuntimeError) as result:
            raise primary
        self.assertIs(primary, result.exception)
        self.assertNotIn('SENSITIVE', json.dumps(context))

    def test_custom_runtime_error_sensitive_getters_are_never_inspected(self):
        calls = []
        class CustomGuardError(RuntimeError):
            def __getattribute__(self, name):
                if name in ('_contact_census_guard', '__dict__', 'args', 'state'):
                    calls.append(name)
                    raise AssertionError('SENSITIVE getter')
                return super().__getattribute__(name)
            def __str__(self):
                calls.append('str')
                raise AssertionError('SENSITIVE message')
            def __repr__(self):
                calls.append('repr')
                raise AssertionError('SENSITIVE repr')
        primary = CustomGuardError()
        self.assertIs(primary, custody.attach_census_guard_context(primary, 'candidate', 'metadata-invalid'))
        self.assertEqual({'exceptionType': 'CustomGuardError'}, custody.failure_context(primary))
        self.assertEqual([], calls)

    def test_malformed_or_custom_census_records_do_not_supply_guard_context(self):
        class UnreadableRecord(dict):
            def get(self, *args):
                raise AssertionError('SENSITIVE getter')
        for record in (UnreadableRecord(stage='candidate'), object(),
                       {'stage': 'SENSITIVE', 'reason': 'metadata-invalid'},
                       {'stage': 'candidate', 'reason': 'SENSITIVE'},
                       {'stage': 'candidate', 'reason': 'metadata-invalid', 'field': 'SENSITIVE'},
                       {'stage': 'candidate', 'reason': 'metadata-invalid', 'invalidKind': 'SENSITIVE'}):
            with self.subTest(recordType=type(record).__name__):
                primary = RuntimeError('SENSITIVE')
                primary._contact_census_guard = record
                self.assertEqual({'exceptionType': 'RuntimeError'}, custody.failure_context(primary))

    def test_census_record_drops_arbitrary_attributes_and_custom_numeric_getters(self):
        class UnreadableMetadata(dict):
            def get(self, *args):
                raise AssertionError('SENSITIVE getter')
        primary = RuntimeError('SENSITIVE')
        primary._contact_census_guard = {'stage': 'candidate', 'reason': 'metadata-invalid',
                                        'numericMetadata': UnreadableMetadata(pid=18), 'message': 'SENSITIVE',
                                        'state': 'SENSITIVE', 'argv': 'SENSITIVE'}
        context = custody.failure_context(primary)
        self.assertEqual('candidate', context['censusGuard']['stage'])
        self.assertNotIn('numericMetadata', context['censusGuard'])
        self.assertNotIn('SENSITIVE', json.dumps(context))

    def test_real_census_runtime_guard_preserves_primary_and_joins_child_without_cleanup_acceptance(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        candidate = {'pid': 18, 'session': -1, 'startTicks': 31, 'uid': 0}
        child = MagicMock(pid=17)
        child.poll.return_value = None
        def join(timeout=None):
            child.returncode = -15
            return -15
        child.wait.side_effect = join
        receipts = []
        failures = []
        attach = custody.attach_census_guard_context
        def observe(failure, *args, **kwargs):
            failures.append(failure)
            return attach(failure, *args, **kwargs)
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=anchor), \
                patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', return_value=candidate) as metadata, \
                patch.object(custody, 'attach_census_guard_context', side_effect=observe), \
                patch.object(Path, 'read_bytes') as environment, \
                patch.object(custody, 'known_alive', return_value=([], [])), \
                patch.object(custody, 'signal_exact') as stop, \
                patch.object(custody.signal, 'SIGKILL', 9, create=True), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(custody.time, 'monotonic', return_value=0), \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: receipts.append(json.loads(text))):
            with self.assertRaises(RuntimeError) as result:
                custody.execute('/sdk/dotnet', ['restore', custody.SOLUTION], 'restore', 3145728, {})
            self.assertIs(failures[0], result.exception)
        self.assertEqual([18, 18], [call.args[0] for call in metadata.call_args_list])
        environment.assert_not_called()
        self.assertEqual([17], [call.args[0]['pid'] for call in stop.call_args_list])
        self.assertEqual('candidate', receipts[0]['failureContext']['censusGuard']['stage'])
        self.assertEqual('session', receipts[0]['failureContext']['censusGuard']['field'])
        self.assertEqual('range', receipts[0]['failureContext']['censusGuard']['invalidKind'])
        self.assertTrue(receipts[0]['retainedChildJoined'])
        self.assertEqual(-15, receipts[0]['actualChildExitCode'])
        self.assertFalse(receipts[0]['cleanupVerified'])
        containers.assert_not_called()

    def old_candidate_controls(self, session, recheck=..., failure=None):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        candidate = {'pid': 18, 'session': session, 'startTicks': 29, 'uid': 1001}
        observed = dict(candidate) if recheck is ... else recheck
        effects = [candidate, failure if failure is not None else observed]
        with ExitStack() as stack:
            stack.enter_context(patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]))
            metadata = stack.enter_context(patch.object(custody, 'process_metadata', side_effect=effects))
            forbidden = [stack.enter_context(patch.object(custody, name, side_effect=AssertionError('Foreign getter forbidden')))
                         for name in ('identity', 'selected_identity', 'live_original_leader', 'signal_exact')]
            forbidden.append(stack.enter_context(patch.object(Path, 'read_bytes', side_effect=AssertionError('Foreign environ forbidden'))))
            forbidden.append(stack.enter_context(patch.object(custody.os, 'kill', side_effect=AssertionError('Foreign signal forbidden'))))
            if failure is not None:
                with self.assertRaises(type(failure)) as result:
                    custody.members(anchor, 'owned-lease', [anchor])
                self.assertIs(failure, result.exception)
            elif recheck is not ...:
                with self.assertRaises(RuntimeError):
                    custody.members(anchor, 'owned-lease', [anchor])
            else:
                self.assertEqual([], custody.members(anchor, 'owned-lease', [anchor]))
            self.assertEqual([18, 18], [call.args[0] for call in metadata.call_args_list])
            for probe in forbidden:
                probe.assert_not_called()

    def test_prephase_different_session_has_zero_foreign_getters(self):
        self.old_candidate_controls(18)

    def test_prephase_same_numeric_session_is_excluded_before_leader_probe(self):
        self.old_candidate_controls(17)

    def test_prephase_candidate_reuse_and_missing_recheck_stay_fail_closed(self):
        for recheck in (None, {'pid': 18, 'session': 18, 'startTicks': 31, 'uid': 1001},
                        {'pid': 18, 'session': 19, 'startTicks': 29, 'uid': 1001},
                        {'pid': 18, 'session': 18, 'startTicks': 29, 'uid': 0}):
            with self.subTest(recheck=recheck):
                self.old_candidate_controls(18, recheck=recheck)

    def test_prephase_unreadable_recheck_keeps_original_failure(self):
        self.old_candidate_controls(18, failure=PermissionError(errno.EACCES, 'synthetic', '/proc/18/stat'))

    def test_equal_and_newer_births_keep_nonce_permission_failure(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        for birth in (30, 31):
            candidate = {'pid': 18, 'session': 18, 'startTicks': birth, 'uid': 1001}
            primary = PermissionError(errno.EACCES, 'synthetic', '/proc/18/environ')
            with self.subTest(birth=birth), patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                    patch.object(custody, 'process_metadata', return_value=candidate), \
                    patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                    patch.object(Path, 'read_bytes', side_effect=primary), \
                    patch.object(custody, 'identity') as identity, patch.object(custody.os, 'kill') as kill:
                with self.assertRaises(PermissionError) as result:
                    custody.members(anchor, 'owned-lease', [anchor])
                self.assertIs(primary, result.exception)
            identity.assert_not_called()
            kill.assert_not_called()

    def test_retained_older_generation_is_verified_before_exclusion(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        retained = {'pid': 18, 'session': 18, 'startTicks': 29, 'uid': 1001, 'executable': '/sdk/retained'}
        primary = PermissionError(errno.EACCES, 'synthetic', '/proc/18/exe')
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', return_value=retained) as metadata, \
                patch.object(custody, 'identity', side_effect=primary), patch.object(Path, 'read_bytes') as environ:
            with self.assertRaises(PermissionError) as result:
                custody.members(anchor, 'owned-lease', [anchor, retained])
            self.assertIs(primary, result.exception)
        metadata.assert_called_once_with(18)
        environ.assert_not_called()

    def test_equal_and_newer_foreign_readable_environment_without_nonce_is_preserved(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        for birth in (30, 31):
            candidate = {'pid': 18, 'session': 18, 'startTicks': birth, 'uid': 1001}
            with self.subTest(birth=birth), ExitStack() as stack:
                stack.enter_context(patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]))
                metadata = stack.enter_context(patch.object(custody, 'process_metadata', return_value=candidate))
                stack.enter_context(patch.object(custody.os, 'getuid', return_value=1001, create=True))
                environment = stack.enter_context(patch.object(Path, 'read_bytes', autospec=True,
                    return_value=b'UNRELATED=synthetic\0'))
                forbidden = [stack.enter_context(patch.object(custody, name,
                    side_effect=AssertionError('Foreign getter forbidden')))
                    for name in ('selected_identity', 'identity', 'live_original_leader', 'signal_exact')]
                forbidden.append(stack.enter_context(patch.object(custody.os, 'kill',
                    side_effect=AssertionError('Foreign signal forbidden'))))
                self.assertEqual([], custody.members(anchor, 'owned-lease', [anchor]))
                environment.assert_called_once_with(Path('/proc/18/environ'))
                metadata.assert_called_once_with(18)
                for probe in forbidden:
                    probe.assert_not_called()

    def test_invalid_anchor_and_missing_original_provenance_are_not_exclusion_proof(self):
        original = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        for value in (True, -1, 9223372036854775808, '30', None):
            with self.subTest(value=value), patch.object(Path, 'iterdir') as listing:
                with self.assertRaises(RuntimeError):
                    custody.members(dict(original, startTicks=value), 'owned-lease', [original])
            listing.assert_not_called()
        for anchor, retained in ((original, []), (dict(original, startTicks=31), [original]),
                                 (dict(original, session=18), [dict(original, session=18)])):
            with self.subTest(anchor=anchor, retained=retained), patch.object(Path, 'iterdir') as listing:
                with self.assertRaises(RuntimeError):
                    custody.members(anchor, 'owned-lease', retained)
            listing.assert_not_called()

    def test_invalid_candidate_numeric_fields_fail_before_foreign_getters(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        valid = {'pid': 18, 'session': 18, 'startTicks': 29, 'uid': 1001}
        for key, value in (('pid', True), ('session', -1), ('startTicks', True), ('startTicks', -1),
                           ('startTicks', 9223372036854775808), ('startTicks', '29'), ('uid', -1), ('uid', 4294967296)):
            with self.subTest(key=key, value=value), patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                    patch.object(custody, 'process_metadata', return_value=dict(valid, **{key: value})), \
                    patch.object(custody, 'identity') as identity, patch.object(Path, 'read_bytes') as environ:
                with self.assertRaises(RuntimeError):
                    custody.members(anchor, 'owned-lease', [anchor])
            identity.assert_not_called()
            environ.assert_not_called()

    def test_invalid_older_recheck_metadata_remains_failure(self):
        for key, value in (('startTicks', True), ('startTicks', None), ('startTicks', -1),
                           ('startTicks', 9223372036854775808), ('uid', True), ('session', '18')):
            candidate = {'pid': 18, 'session': 18, 'startTicks': 29, 'uid': 1001}
            with self.subTest(key=key, value=value):
                self.old_candidate_controls(18, recheck=dict(candidate, **{key: value}))

    def test_real_census_nonce_failure_keeps_primary_and_known_child_settlement(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        candidate = {'pid': 18, 'session': 18, 'startTicks': 31, 'uid': 1001}
        primary = PermissionError(errno.EACCES, 'synthetic', '/proc/18/environ')
        child = MagicMock(pid=17)
        child.poll.return_value = None
        def join(timeout=None):
            child.returncode = -15
            return -15
        child.wait.side_effect = join
        receipts = []
        with patch.object(custody, 'physical_kib', return_value=4194304), \
                patch.object(custody, 'source_pins', return_value={'testedCommit': 'synthetic'}), \
                patch.object(custody.subprocess, 'Popen', return_value=child), \
                patch.object(custody, 'identity', return_value=anchor), \
                patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', return_value=candidate), \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', side_effect=primary), \
                patch.object(custody, 'known_alive', return_value=([], [])), \
                patch.object(custody, 'signal_exact') as stop, \
                patch.object(custody.signal, 'SIGKILL', 9, create=True), \
                patch.object(custody, 'cleanup_containers') as containers, \
                patch.object(custody.time, 'monotonic', return_value=0), \
                patch.object(Path, 'mkdir'), \
                patch.object(Path, 'write_text', side_effect=lambda text: receipts.append(json.loads(text))):
            with self.assertRaises(PermissionError) as result:
                custody.execute('/sdk/dotnet', ['restore', custody.SOLUTION], 'restore', 3145728, {})
            self.assertIs(primary, result.exception)
        self.assertEqual([17], [call.args[0]['pid'] for call in stop.call_args_list])
        child.wait.assert_any_call(timeout=5)
        self.assertTrue(receipts[0]['retainedChildJoined'])
        self.assertEqual(-15, receipts[0]['actualChildExitCode'])
        self.assertFalse(receipts[0]['cleanupVerified'])
        containers.assert_not_called()

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
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        foreign = {'pid': 1, 'session': 1, 'startTicks': 1, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/1')]), \
                patch.object(custody, 'process_metadata', return_value=foreign), \
                patch.object(custody, 'identity', side_effect=PermissionError('synthetic foreign executable')) as probe, \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', return_value=b'UNRELATED=synthetic\0'):
            self.assertEqual([], custody.members(anchor, 'synthetic-lease', [anchor]))
        probe.assert_not_called()

    def test_unreadable_owned_executable_remains_failure(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        owned = {'pid': 17, 'session': 17, 'startTicks': 30, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/17')]), \
                patch.object(custody, 'process_metadata', return_value=owned), \
                patch.object(custody, 'identity', side_effect=PermissionError('synthetic owned executable')):
            with self.assertRaises(PermissionError):
                custody.members(anchor, 'synthetic-lease', [anchor])

    def test_unreadable_same_uid_nonce_remains_uncertain_failure(self):
        anchor = {'pid': 17, 'session': 17, 'startTicks': 30, 'uid': 1001, 'executable': '/sdk/dotnet'}
        uncertain = {'pid': 18, 'session': 18, 'startTicks': 31, 'state': 'S', 'uid': 1001}
        with patch.object(Path, 'iterdir', return_value=[Path('/proc/18')]), \
                patch.object(custody, 'process_metadata', return_value=uncertain), \
                patch.object(custody.os, 'getuid', return_value=1001, create=True), \
                patch.object(Path, 'read_bytes', side_effect=PermissionError('synthetic unavailable ownership')):
            with self.assertRaises(PermissionError):
                custody.members(anchor, 'synthetic-lease', [anchor])

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
