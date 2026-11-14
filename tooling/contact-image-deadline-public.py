"""Fail-closed unauthenticated run-clock admission for Contact image jobs."""
import datetime as dt
import email.utils
import json
import os
import re
import sys
import urllib.request
import uuid

ROOT = 'https://api.github.com/repos/MALIEV-Co-Ltd/Legacy.Maliev.ContactService/actions/runs/'
LIMIT = 262144


def fetch(url):
    separator = '&' if '?' in url else '?'
    request = urllib.request.Request(f'{url}{separator}contact_admission_nonce={uuid.uuid4().hex}', headers={'Cache-Control': 'no-cache',
        'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'contact-image-admission'})
    with urllib.request.urlopen(request, timeout=10) as response:
        if response.status != 200:
            raise ValueError('metadata status')
        date = email.utils.parsedate_to_datetime(response.headers['Date']).astimezone(dt.timezone.utc)
        now = dt.datetime.now(dt.timezone.utc)
        if abs((now - date).total_seconds()) > 30:
            delta = int((now - date).total_seconds())
            print(f'Contact server date offset seconds: {max(-86400, min(86400, delta))}', file=sys.stderr)
            raise ValueError('stale or skewed server clock')
        body = response.read(LIMIT + 1)
        if len(body) > LIMIT:
            raise ValueError('oversized metadata')
        # Recheck after the body read: a slow response must not reuse a stale Date.
        if abs((dt.datetime.now(dt.timezone.utc) - date).total_seconds()) > 30:
            raise ValueError('stale or skewed server clock')
    return json.loads(body), date


def evaluate(phase, run_id, attempt, job_name, runner_name, run, jobs, now):
    if phase not in ('build', 'startup'):
        raise ValueError('unknown phase')
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if job_name != 'image-build' or not runner_name:
        raise ValueError('unexpected job identity')
    if run['id'] != int(run_id) or run['run_attempt'] != int(attempt):
        raise ValueError('run attempt mismatch')
    rows = jobs['jobs']
    if jobs['total_count'] != len(rows) or len(rows) > 100:
        raise ValueError('incomplete job inventory')
    matches = [x for x in rows if x['name'] == job_name and x['runner_name'] == runner_name
               and x['run_id'] == int(run_id) and x['run_attempt'] == int(attempt)
               and x['status'] == 'in_progress']
    if len(matches) != 1:
        raise ValueError('current job not uniquely identified')
    parse = lambda value: dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    run_start, job_start = parse(run['run_started_at']), parse(matches[0]['started_at'])
    if run_start.tzinfo is None or job_start.tzinfo is None or not run_start <= job_start <= now:
        raise ValueError('uncertain run/job timing')
    remaining = (run_start + dt.timedelta(minutes=55) - now).total_seconds()
    # Reserve includes admission's one-minute cap, 30s clock skew, remaining
    # phases, cleanup, artifact upload and two minutes post-run slack.
    required = {'build': 46 * 60, 'startup': 13 * 60}[phase]
    if not 0 < remaining <= 55 * 60 or remaining < required:
        raise ValueError('insufficient reserve')
    return int(remaining)


def main():
    if len(sys.argv) != 2:
        raise ValueError('missing phase')
    run_id, attempt = os.environ['GITHUB_RUN_ID'], os.environ['GITHUB_RUN_ATTEMPT']
    if not re.fullmatch(r'[1-9][0-9]*', run_id) or not re.fullmatch(r'[1-9][0-9]*', attempt):
        raise ValueError('invalid run identity')
    if os.environ['GITHUB_REPOSITORY'] != 'MALIEV-Co-Ltd/Legacy.Maliev.ContactService':
        raise ValueError('unexpected repository')
    prefix = f'{ROOT}{run_id}/attempts/{attempt}'
    run, _ = fetch(prefix)
    jobs, now = fetch(prefix + '/jobs?per_page=100')
    remaining = evaluate(sys.argv[1], run_id, attempt, os.environ['GITHUB_JOB'],
                         os.environ['RUNNER_NAME'], run, jobs, now)
    print(f'Admitted {sys.argv[1]}: conservative remaining={remaining}s')


def safe_failure(error):
    allowed = {'metadata status', 'stale or skewed server clock', 'oversized metadata',
               'unknown phase', 'invalid run identity', 'unexpected job identity',
               'run attempt mismatch', 'incomplete job inventory',
               'current job not uniquely identified', 'uncertain run/job timing',
               'insufficient reserve', 'missing phase', 'unexpected repository'}
    if type(error) is ValueError and str(error) in allowed:
        return str(error)
    return type(error).__name__


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Contact image time admission refused: {safe_failure(error)}', file=sys.stderr)
        sys.exit(1)
