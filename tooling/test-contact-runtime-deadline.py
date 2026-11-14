"""Offline controls for Contact runtime deadline admission; no network or processes."""
import datetime as dt
import email.utils
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

spec = importlib.util.spec_from_file_location('deadline', Path(__file__).with_name('contact-runtime-deadline.py'))
deadline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deadline)
NOW = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)

class FrozenDateTime(dt.datetime):
    @classmethod
    def now(cls, tz=None): return NOW

class Response:
    status = 200
    def __init__(self, date=NOW, body=b'{}', headers=None):
        self.headers = headers if headers is not None else {'Date': email.utils.format_datetime(date, usegmt=True)}
        self.body = body
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def read(self, size): return self.body[:size]

class AdmissionTests(unittest.TestCase):
    def fetch(self, value):
        with patch.object(deadline.dt, 'datetime', FrozenDateTime), patch.object(deadline.urllib.request, 'urlopen', side_effect=value if isinstance(value, Exception) else None, return_value=None if isinstance(value, Exception) else value):
            return deadline.fetch('https://example.invalid')

    def test_unique_cache_nonce_preserves_query_and_no_authorization(self):
        observed = []
        def capture(request, timeout):
            observed.append((request.full_url, timeout, dict(request.header_items())))
            return Response()
        with patch.object(deadline.dt, 'datetime', FrozenDateTime), patch.object(deadline.urllib.request, 'urlopen', side_effect=capture):
            deadline.fetch('https://example.invalid/run')
            deadline.fetch('https://example.invalid/jobs?per_page=100')
        self.assertRegex(observed[0][0], r'^https://example[.]invalid/run\?contact_admission_nonce=[0-9a-f]{32}$')
        self.assertRegex(observed[1][0], r'^https://example[.]invalid/jobs\?per_page=100&contact_admission_nonce=[0-9a-f]{32}$')
        self.assertNotEqual(observed[0][0].split('=')[-1], observed[1][0].split('=')[-1])
        self.assertTrue(all(timeout == 10 and 'Authorization' not in headers for _, timeout, headers in observed))

    def test_429_and_timeout_fail_closed(self):
        for error in (HTTPError('https://example.invalid',429,'rate limited',None,None), TimeoutError()):
            with self.subTest(error=type(error).__name__), self.assertRaises(type(error)):
                self.fetch(error)

    def test_missing_or_skewed_date_fails_closed(self):
        for response in (Response(headers={}), Response(NOW + dt.timedelta(seconds=31)), Response(NOW - dt.timedelta(seconds=31))):
            with self.subTest(headers=response.headers), self.assertRaises((KeyError, ValueError)):
                self.fetch(response)

    def test_delayed_body_cannot_reuse_stale_date(self):
        class AdvancingDateTime(dt.datetime):
            calls = 0
            @classmethod
            def now(cls, tz=None):
                cls.calls += 1
                return NOW if cls.calls == 1 else NOW + dt.timedelta(seconds=31)
        with patch.object(deadline.dt, 'datetime', AdvancingDateTime), patch.object(deadline.urllib.request, 'urlopen', return_value=Response()):
            with self.assertRaisesRegex(ValueError, 'stale or skewed server clock'):
                deadline.fetch('https://example.invalid')

    def test_oversized_response_fails_closed(self):
        with self.assertRaises(ValueError): self.fetch(Response(body=b'x'*(deadline.LIMIT+1)))

    def fixtures(self, elapsed):
        start = NOW-dt.timedelta(seconds=elapsed)
        run = {'id': 123, 'run_attempt': 2, 'run_started_at': start.isoformat()}
        job = {'name':'validate','runner_name':'runner-1','run_id':123,'run_attempt':2,
               'status':'in_progress','started_at':(start+dt.timedelta(seconds=1)).isoformat()}
        return run, {'total_count':1,'jobs':[job]}

    def evaluate(self, phase, elapsed, run_id='123', attempt='2', mutate=None):
        run,jobs=self.fixtures(elapsed)
        if mutate: mutate(run,jobs)
        return deadline.evaluate(phase,run_id,attempt,'validate','runner-1',run,jobs,NOW)

    def test_exact_reserve_boundaries(self):
        for phase, exact in [('validate',300)]:
            with self.subTest(phase=phase):
                self.assertEqual(3600-exact,self.evaluate(phase,exact))
                with self.assertRaises(ValueError): self.evaluate(phase,exact+1)

    def test_invalid_and_mismatched_ids(self):
        for run_id,attempt in [('0','2'),('x','2'),('123','0'),('123','3')]:
            with self.subTest(run_id=run_id,attempt=attempt), self.assertRaises(ValueError):
                self.evaluate('validate',100,run_id,attempt)
        with self.assertRaises(ValueError): self.evaluate('validate',100,mutate=lambda r,j: j['jobs'][0].update(run_id=999))

    def test_missing_duplicate_and_wrong_job_order(self):
        def duplicate(r,j): j['jobs'].append(dict(j['jobs'][0])); j['total_count']=2
        def bad_order(r,j): j['jobs'][0]['started_at']=(NOW-dt.timedelta(seconds=101)).isoformat()
        def truncated(r,j): j['total_count']=2
        for mutate in (duplicate,bad_order,truncated):
            with self.subTest(mutate=mutate.__name__), self.assertRaises(ValueError): self.evaluate('validate',100,mutate=mutate)

if __name__ == '__main__': unittest.main()
