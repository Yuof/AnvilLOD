#!/usr/bin/env python3
import hashlib
import importlib.util
import json
import pathlib
import tempfile
import unittest
import zipfile

tools = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('virustotal_scan', tools / 'virustotal-scan.py')
scan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scan)

CLEAN = {'EngineA': {'category': 'undetected', 'result': None}, 'EngineB': {'category': 'harmless', 'result': None},
         'EngineC': {'category': 'type-unsupported', 'result': None}}
FLAGGED = {**CLEAN, 'BitDefender': {'category': 'malicious', 'result': 'Gen:Variant.Ulise.617802'}}


class Server:
    """Answers the VirusTotal calls from a table of known files, and records them."""

    def __init__(self, known=None, after_analysis=None, busy=0):
        self.known, self.after_analysis, self.busy, self.calls, self.polls = known or {}, after_analysis or {}, busy, [], 0

    def send(self, method, url, body, headers):
        assert headers['x-apikey'] == 'key'
        path = url.removeprefix(scan.API)
        self.calls.append((method, path))
        if self.busy:
            self.busy -= 1
            return 429, b'{}'
        if method == 'GET' and path.startswith('/files/'):
            sha = path.split('/')[2]
            if sha not in self.known:
                return 404, b'{}'
            return 200, json.dumps({'data': {'attributes': {'last_analysis_results': self.known[sha], 'last_analysis_date': 1795000000}}}).encode()
        if method == 'POST' and path == '/files':
            assert b'filename="PenguLOD.dll"' in body and headers['content-type'].startswith('multipart/form-data; boundary=')
            sha = hashlib.sha256(body.split(b'\r\n\r\n', 1)[1].rsplit(b'\r\n--', 1)[0]).hexdigest()
            return 200, json.dumps({'data': {'id': 'analysis-' + sha}}).encode()
        if method == 'POST' and path.endswith('/analyse'):
            return 200, json.dumps({'data': {'id': 'analysis-' + path.split('/')[2]}}).encode()
        if method == 'GET' and path.startswith('/analyses/'):
            self.polls += 1
            sha = path.split('analysis-')[1]
            if self.polls < 2:
                return 200, json.dumps({'data': {'attributes': {'status': 'queued', 'results': {}}}}).encode()
            return 200, json.dumps({'data': {'attributes': {'status': 'completed', 'results': self.after_analysis.get(sha, CLEAN)}}}).encode()
        return 400, b'unexpected'


class ScanTests(unittest.TestCase):
    def run_scan(self, server, files, *options):
        slept = []
        now = [0.0]

        def sleep(seconds):
            slept.append(seconds)
            now[0] += seconds

        client = scan.Client('key', send=server.send, sleep=sleep, clock=lambda: now[0])
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for name, content in files.items():
                path = pathlib.Path(tmp) / name
                if name.endswith('.zip'):
                    with zipfile.ZipFile(path, 'w') as archive:
                        for inner, data in content.items():
                            archive.writestr(inner, data)
                else:
                    path.write_bytes(content)
                paths.append(str(path))
            summary = pathlib.Path(tmp) / 'summary.md'
            code = scan.main([*paths, '--summary', str(summary), *options], client=client, environment={})
            return code, summary.read_text(), slept

    def test_known_clean_files_pass_without_an_upload(self):
        dll = b'MZ clean'
        server = Server(known={hashlib.sha256(dll).hexdigest(): CLEAN})
        code, summary, _ = self.run_scan(server, {'PenguLOD.dll': dll})
        self.assertEqual(0, code)
        self.assertEqual([('GET', '/files/' + hashlib.sha256(dll).hexdigest())], server.calls)
        self.assertIn('| 0 of 2 | last analysed 2026-', summary)

    def test_a_flagged_dll_inside_a_package_fails_and_names_the_engine(self):
        clean, bad = b'MZ one', b'MZ two'
        server = Server(known={hashlib.sha256(clean).hexdigest(): CLEAN, hashlib.sha256(bad).hexdigest(): FLAGGED})
        package = {'1.7.x/SKSE/Plugins/PenguLOD.dll': clean, '1.4.15-VR/SKSE/Plugins/PenguLOD.dll': bad, 'README.md': b'text'}
        code, summary, _ = self.run_scan(server, {'PenguLOD-SKSE.zip': package})
        self.assertEqual(1, code)
        self.assertEqual(2, len(server.calls))          # the two DLLs, not the README
        self.assertIn('`PenguLOD-SKSE.zip: 1.4.15-VR/SKSE/Plugins/PenguLOD.dll` is flagged by:', summary)
        self.assertIn('- BitDefender: Gen:Variant.Ulise.617802', summary)
        self.assertEqual(0, self.run_scan(server, {'PenguLOD-SKSE.zip': package}, '--report-only')[0])

    def test_an_unknown_file_is_uploaded_and_its_analysis_awaited(self):
        dll = b'MZ new'
        sha = hashlib.sha256(dll).hexdigest()
        server = Server(after_analysis={sha: FLAGGED})
        code, summary, _ = self.run_scan(server, {'PenguLOD.dll': dll})
        self.assertEqual(1, code)
        self.assertEqual([('GET', f'/files/{sha}'), ('POST', '/files'), ('GET', f'/analyses/analysis-{sha}'), ('GET', f'/analyses/analysis-{sha}')], server.calls)
        self.assertIn('| 1 of 3 | uploaded now |', summary)

    def test_reanalysis_replaces_a_stale_clean_result(self):
        dll = b'MZ stale'
        sha = hashlib.sha256(dll).hexdigest()
        server = Server(known={sha: CLEAN}, after_analysis={sha: FLAGGED})
        code, summary, _ = self.run_scan(server, {'PenguLOD.dll': dll}, '--reanalyse')
        self.assertEqual(1, code)
        self.assertIn(('POST', f'/files/{sha}/analyse'), server.calls)
        self.assertIn('analysed again now', summary)

    def test_requests_are_paced_and_a_rate_limit_answer_is_tried_again(self):
        dll = b'MZ paced'
        server = Server(known={hashlib.sha256(dll).hexdigest(): CLEAN}, busy=1)
        code, _, slept = self.run_scan(server, {'PenguLOD.dll': dll})
        self.assertEqual(0, code)
        self.assertEqual(2, len(server.calls))
        self.assertIn(60, slept)

    def test_without_a_key_nothing_is_checked_and_the_build_passes(self):
        self.assertEqual(0, scan.main(['missing.dll'], environment={}))


if __name__ == '__main__':
    unittest.main()
