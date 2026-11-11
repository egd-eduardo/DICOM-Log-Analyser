#!/usr/bin/env python3
"""Repository safeguard: fails when committed files look like secrets, real logs, indexes or PHI.

Checks every file tracked by git (or every file when git is unavailable):
  * forbidden file types (databases, archives, DICOM, certificates/keys, .env)
  * private keys, cloud/API tokens, hard-coded passwords and connection strings
  * PHI-like content: DICOM UIDs outside the synthetic 2.25 root and the DICOM standard root (1.2.840.10008),
    patient names that are not SYNTHETIC^..., and public IPv4 addresses outside documentation/private ranges.
This is a heuristic safety net, not a guarantee. It complements (does not replace) review before every commit.
"""
import ipaddress, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FORBIDDEN_EXT = {'.db', '.sqlite', '.db-wal', '.db-shm', '.zip', '.gz', '.7z', '.rar', '.dcm', '.pfx', '.p12', '.pem', '.key', '.crt', '.evtx'}
SECRET_PATTERNS = [
    (re.compile(r'-----BEGIN (RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'), 'private key'),
    (re.compile(r'AKIA[0-9A-Z]{16}'), 'AWS access key'),
    (re.compile(r'gh[pousr]_[A-Za-z0-9]{36,}'), 'GitHub token'),
    (re.compile(r'xox[abprs]-[A-Za-z0-9-]{10,}'), 'Slack token'),
    (re.compile(r'(?i)(password|passwd|pwd|secret|client_secret)\s*[:=]\s*["\'][^"\'\s]{6,}["\']'), 'hard-coded credential'),
    (re.compile(r'(?i)(Password|Pwd)=[^;"\s]{4,};'), 'connection string password'),
]
UID = re.compile(r'(?<![\d.])([12](?:\.\d+){4,})(?![\d.])')
ALLOWED_UID_PREFIXES = ('2.25.', '1.2.840.10008.')
PERSON = re.compile(r'(?i)PatientName=([^\s,;]+)')
IPV4 = re.compile(r'(?<![\d.])((?:\d{1,3}\.){3}\d{1,3})(?![\d.])')
DOC_NETS = [ipaddress.ip_network(n) for n in ('192.0.2.0/24', '198.51.100.0/24', '203.0.113.0/24')]
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules', 'artifacts', 'TestResults'}
SKIP_FILES = {'packages.lock.json', 'package-lock.json'}
# The only binary files allowed: icons generated from Ui/favicon.svg by tools/icons/make-icons.cjs. Each must start
# with the stated signature, so a screenshot or export renamed to one of these paths is still refused.
ALLOWED_BINARY = {
    'src/DicomLogAnalyser.Web/Assets/app.ico': b'\x00\x00\x01\x00',
    'src/DicomLogAnalyser.Web/Ui/apple-touch-icon.png': b'\x89PNG\r\n\x1a\n',
}


def files():
    try:
        out = subprocess.run(['git', 'ls-files', '-co', '--exclude-standard'], cwd=ROOT, capture_output=True, text=True, check=True).stdout
        return [f for f in out.splitlines() if f and os.path.isfile(os.path.join(ROOT, f))]
    except (OSError, subprocess.CalledProcessError):
        result = []
        for d, dirs, fs in os.walk(ROOT):
            dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
            result += [os.path.relpath(os.path.join(d, f), ROOT) for f in fs]
        return result


def allowed_image(rel, signature):
    """An allowed icon: the expected signature and a plausible size (a small generated icon, not a photo)."""
    path = os.path.join(ROOT, rel)
    with open(path, 'rb') as f:
        head = f.read(len(signature))
    return head == signature and os.path.getsize(path) <= 64 * 1024


def ip_ok(text):
    try:
        ip = ipaddress.ip_address(text)
    except ValueError:
        return True  # not an address (e.g. a version number)
    return ip.is_private or ip.is_loopback or ip.is_unspecified or ip.is_link_local or any(ip in n for n in DOC_NETS)


def main():
    problems = []
    checked = files()
    for rel in checked:
        name = os.path.basename(rel)
        if name in SKIP_FILES or rel == 'scripts/check-secrets.py':
            continue
        ext = os.path.splitext(name)[1].lower()
        if ext in FORBIDDEN_EXT or name == '.env':
            problems.append(f'{rel}: file type must not be committed')
            continue
        try:
            text = open(os.path.join(ROOT, rel), encoding='utf-8', errors='replace').read()
        except OSError:
            continue
        if '\0' in text[:4096]:
            norm = rel.replace(os.sep, '/')
            if norm in ALLOWED_BINARY and allowed_image(rel, ALLOWED_BINARY[norm]):
                continue
            problems.append(f'{rel}: binary file')
            continue
        for n, line in enumerate(text.splitlines(), 1):
            for pattern, what in SECRET_PATTERNS:
                if pattern.search(line):
                    problems.append(f'{rel}:{n}: possible {what}')
            for m in UID.finditer(line):
                if not m.group(1).startswith(ALLOWED_UID_PREFIXES):
                    problems.append(f'{rel}:{n}: DICOM UID outside the synthetic 2.25 root')
            for m in PERSON.finditer(line):
                if not m.group(1).upper().startswith('SYNTHETIC'):
                    problems.append(f'{rel}:{n}: patient name that is not SYNTHETIC^...')
            for m in IPV4.finditer(line):
                if not ip_ok(m.group(1)):
                    problems.append(f'{rel}:{n}: public IPv4 address outside documentation ranges')
    if problems:
        print('Secret/PHI scan FAILED:')
        for p in problems:
            print('  ' + p)
        return 1
    print(f'Secret/PHI scan passed ({len(checked)} files checked).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
