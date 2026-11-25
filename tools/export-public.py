#!/usr/bin/env python3
# Copy the sources to the folder of the public repository (GitHub), without the git history of this one.
#
#   python tools/export-public.py <folder of the public repository>
#
# The files: the tracked files and the new files that git does not ignore. Not copied: VERSION (each server
# has its own build number), the firmware builds (wwwroot/firmware) and the Visual Studio user file.
# The public repository is in English: README.en.md goes there as README.md, and the Ukrainian README.md
# stays here. In the target folder the script keeps .git and the review files (CHANGES.diff, COMMIT_MSG.txt)
# and deletes the other files that the source does not have; then "git status" there shows the change.
#
# Before the copy the script looks for the names of the local network and for secrets in the text files:
# one regular expression per line in .public-deny (in the project folder, not in git; "#" starts a comment).
# With a match it shows the file and the line and copies nothing.
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
SKIP_PREFIXES = ('wwwroot/firmware/',)
SKIP_FILES = {'VERSION', 'trmnl-server.csproj.user'}
# A source file under another name in the target; the source file of that other name does not go there.
PUBLIC_NAMES = {'README.en.md': 'README.md'}
KEEP_IN_TARGET = {'.git', 'CHANGES.diff', 'COMMIT_MSG.txt'}
BINARY = ('.png', '.ttf', '.bin', '.jpg', '.ico', '.pdf', '.bmp')


def source_files():
    out = subprocess.run(['git', '-C', ROOT, 'ls-files', '-co', '--exclude-standard', '-z'],
                         check=True, capture_output=True).stdout.decode('utf-8')
    files = sorted({f for f in out.split('\0') if f})
    files = [f for f in files
             if not f.startswith(SKIP_PREFIXES) and f not in SKIP_FILES and os.path.isfile(os.path.join(ROOT, f))]
    replaced = {target for source, target in PUBLIC_NAMES.items() if source in files}
    return [f for f in files if f not in replaced]


def target_name(rel):
    return PUBLIC_NAMES.get(rel, rel)


def check_translation():
    """Warn when the Ukrainian README changed after the English one: the translation may be old."""
    uk, en = os.path.join(ROOT, 'README.md'), os.path.join(ROOT, 'README.en.md')
    if os.path.isfile(uk) and os.path.isfile(en) and os.path.getmtime(uk) > os.path.getmtime(en) + 1:
        print('Note: README.md changed after README.en.md: update the English README for the public repository.')


def deny_patterns():
    path = os.path.join(ROOT, '.public-deny')
    if not os.path.isfile(path):
        sys.exit('No .public-deny: write the names of the local network there first (one regular expression a line).')
    patterns = []
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith('#'):
                patterns.append(re.compile(line, re.IGNORECASE))
    return patterns


def check(files, patterns):
    found = 0
    for rel in files:
        if rel.lower().endswith(BINARY):
            continue
        with open(os.path.join(ROOT, rel), 'rb') as f:
            text = f.read().decode('utf-8', errors='replace')
        for number, line in enumerate(text.splitlines(), 1):
            for p in patterns:
                if p.search(line):
                    print('%s:%d: %s  [%s]' % (rel, number, line.strip()[:120], p.pattern))
                    found += 1
    return found


def main():
    if len(sys.argv) != 2:
        sys.exit('usage: export-public.py <folder>')
    target = os.path.abspath(sys.argv[1])
    if target == ROOT or target.startswith(ROOT + os.sep):
        sys.exit('The target must be outside the project folder.')
    files = source_files()
    found = check(files, deny_patterns())
    if found:
        sys.exit('%d lines have local names or secrets: nothing is copied.' % found)
    check_translation()

    os.makedirs(target, exist_ok=True)
    wanted = {target_name(f) for f in files}
    # Delete the files of the target that the source does not have.
    for dirpath, dirnames, filenames in os.walk(target, topdown=True):
        rel_dir = os.path.relpath(dirpath, target).replace(os.sep, '/')
        if rel_dir == '.':
            dirnames[:] = [d for d in dirnames if d not in KEEP_IN_TARGET]
        for name in filenames:
            rel = name if rel_dir == '.' else rel_dir + '/' + name
            if rel not in wanted and not (rel_dir == '.' and name in KEEP_IN_TARGET):
                os.remove(os.path.join(dirpath, name))
    for dirpath, dirnames, filenames in os.walk(target, topdown=False):
        if dirpath != target and not os.listdir(dirpath) and '.git' not in dirpath.split(os.sep):
            os.rmdir(dirpath)
    # Copy the files byte for byte (the line ends stay as in the working copy).
    for rel in files:
        dst = os.path.join(target, target_name(rel))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(ROOT, rel), dst)
    print('%d files are in %s' % (len(files), target))


if __name__ == '__main__':
    main()
