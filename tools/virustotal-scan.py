#!/usr/bin/env python3
"""Check the game plugin DLLs on VirusTotal and say which engines flag them.

Give it DLLs, or SKSE packages (.zip) whose DLLs are checked. A file VirusTotal has not seen is uploaded.
The API key is read from VIRUSTOTAL_API_KEY; without one nothing is checked and the exit code is 0, so
that builds of forks, which do not get the secret, still pass.

Exit code 1 means at least one engine flags a file. --report-only reports the same and exits 0.
"""
import argparse
import hashlib
import json
import os
import pathlib
import sys
import time
import urllib.error
import urllib.request
import uuid
import zipfile

API = 'https://www.virustotal.com/api/v3'
GUI = 'https://www.virustotal.com/gui/file/'
# The free API allows four requests a minute.
SECONDS_BETWEEN_REQUESTS = 16
FLAGGED = ('malicious', 'suspicious')


class Client:
    """The VirusTotal calls this tool needs. `send` and `sleep` are replaced in tests."""

    def __init__(self, key, send=None, sleep=time.sleep, clock=time.monotonic):
        self.key, self.sleep, self.clock, self.last = key, sleep, clock, None
        self.send = send or self._send

    def _send(self, method, url, body, headers):
        request = urllib.request.Request(url, data=body, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.read()

    def call(self, method, path, body=None, headers=None):
        """One request, paced to the rate limit. Returns the JSON, or None for 404."""
        for attempt in range(6):
            if self.last is not None:
                self.sleep(max(0, SECONDS_BETWEEN_REQUESTS - (self.clock() - self.last)))
            self.last = self.clock()
            status, content = self.send(method, API + path, body, {'x-apikey': self.key, 'accept': 'application/json', **(headers or {})})
            if status == 404:
                return None
            if status == 429 or status >= 500:
                self.sleep(60)
                continue
            if status != 200:
                raise SystemExit(f'VirusTotal answered {status} for {method} {path}: {content[:200].decode(errors="replace")}')
            return json.loads(content)
        raise SystemExit(f'VirusTotal kept refusing {method} {path} (rate limit or server error).')

    def file(self, sha256):
        return self.call('GET', f'/files/{sha256}')

    def upload(self, name, content):
        boundary = uuid.uuid4().hex
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n'
                'Content-Type: application/octet-stream\r\n\r\n').encode() + content + f'\r\n--{boundary}--\r\n'.encode()
        return self.call('POST', '/files', body, {'content-type': f'multipart/form-data; boundary={boundary}'})['data']['id']

    def reanalyse(self, sha256):
        return self.call('POST', f'/files/{sha256}/analyse')['data']['id']

    def analysis(self, analysis_id, minutes):
        """Waits for an analysis to finish and returns its attributes."""
        deadline = self.clock() + minutes * 60
        while True:
            attributes = self.call('GET', f'/analyses/{analysis_id}')['data']['attributes']
            if attributes.get('status') == 'completed':
                return attributes
            if self.clock() > deadline:
                raise SystemExit(f'VirusTotal did not finish analysis {analysis_id} within {minutes} minutes.')
            self.sleep(30)


def collect(paths):
    """(label, bytes) for every DLL named, or inside a package named."""
    files = []
    for path in map(pathlib.Path, paths):
        if path.suffix.lower() == '.zip':
            with zipfile.ZipFile(path) as archive:
                for name in sorted(n for n in archive.namelist() if n.lower().endswith('.dll')):
                    files.append((f'{path.name}: {name}', archive.read(name)))
        else:
            files.append((str(path), path.read_bytes()))
    if not files:
        raise SystemExit('No DLL to check.')
    return files


def verdict(results):
    """(engines that flag the file as 'Engine: label', engines that gave a verdict)."""
    flagged = sorted(f'{engine}: {result.get("result") or result.get("category")}'
                     for engine, result in results.items() if result.get('category') in FLAGGED)
    judged = sum(1 for result in results.values() if result.get('category') in (*FLAGGED, 'undetected', 'harmless'))
    return flagged, judged


def check(client, files, reanalyse, minutes):
    rows = []
    for label, content in files:
        sha256 = hashlib.sha256(content).hexdigest()
        known = client.file(sha256)
        if known is None:
            results = client.analysis(client.upload(pathlib.PurePosixPath(label.split(': ')[-1]).name, content), minutes)['results']
            source = 'uploaded now'
        elif reanalyse:
            results = client.analysis(client.reanalyse(sha256), minutes)['results']
            source = 'analysed again now'
        else:
            attributes = known['data']['attributes']
            results = attributes.get('last_analysis_results', {})
            when = attributes.get('last_analysis_date')
            source = 'last analysed ' + (time.strftime('%Y-%m-%d %H:%M UTC', time.gmtime(when)) if when else 'at an unknown time')
        flagged, judged = verdict(results)
        rows.append({'file': label, 'sha256': sha256, 'flagged': flagged, 'engines': judged, 'source': source})
    return rows


def report(rows):
    lines = ['| File | Flagged | Result from | Report |', '| --- | --- | --- | --- |']
    for row in rows:
        lines.append(f'| `{row["file"]}` | {len(row["flagged"])} of {row["engines"]} | {row["source"]} | [{row["sha256"][:12]}]({GUI}{row["sha256"]}) |')
    for row in rows:
        if row['flagged']:
            lines += ['', f'`{row["file"]}` is flagged by:', ''] + [f'- {entry}' for entry in row['flagged']]
    return '\n'.join(lines) + '\n'


def main(arguments=None, client=None, environment=os.environ):
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('paths', nargs='+', help='DLLs, or SKSE packages (.zip)')
    parser.add_argument('--reanalyse', action='store_true', help='have VirusTotal analyse known files again; a first result can change within hours')
    parser.add_argument('--report-only', action='store_true', help='exit 0 even when a file is flagged')
    parser.add_argument('--wait-minutes', type=int, default=15, help='longest wait for one analysis (default 15)')
    parser.add_argument('--summary', help='also append the report to this file (the job summary in CI)')
    options = parser.parse_args(arguments)
    key = environment.get('VIRUSTOTAL_API_KEY', '').strip()
    if client is None:
        if not key:
            print('VIRUSTOTAL_API_KEY is not set; nothing was checked.')
            return 0
        client = Client(key)
    rows = check(client, collect(options.paths), options.reanalyse, options.wait_minutes)
    text = report(rows)
    print(text)
    if options.summary:
        with open(options.summary, 'a', encoding='utf-8') as summary:
            summary.write('## VirusTotal\n\n' + text)
    flagged = [row for row in rows if row['flagged']]
    if flagged:
        print(f'{len(flagged)} of {len(rows)} files are flagged.', file=sys.stderr)
    return 1 if flagged and not options.report_only else 0


if __name__ == '__main__':
    sys.exit(main())
