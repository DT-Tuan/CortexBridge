#!/usr/bin/env python3
"""
migrate-project follow-up: surgical, JSON-aware path rewrite inside JSONL.

The migration's `install.sh` places JSONL files at the correct VPS encoded
path, but every JSON record still carries PC-side absolute paths baked into
fields like `cwd`, `file_path`, `transcriptPath`. CortexBridge's
`SessionScanner` derives the project name from `cwd` (basename), so leaving
`e:\\projects\\MyApp` makes the dashboard display the raw Windows string —
and flags the session "imported" / read-only because cwd is non-POSIX.

This script walks each JSON-parsed record, rewrites any STRING value that
begins with a declared `from` prefix to the matching `to` prefix, and
normalises remaining backslashes in the tail to forward slashes so the
resulting path is valid POSIX.

It is JSON-aware (works on parsed objects, not raw text) → no false positives
on incidental backslash sequences elsewhere; and it backs up the original to
`<file>.pre-rewrite.bak` per the existing CortexBridge convention.

Usage:
  rewrite-paths.py <jsonl-or-dir> --map "FROM=TO" [--map "FROM2=TO2" ...]

Example (MyApp PC -> VPS):
  rewrite-paths.py ~/.claude/projects/-home-youruser-workspace-MyApp \\
    --map "e:\\projects\\MyApp=/home/youruser/workspace/MyApp" \\
    --map "E:\\projects\\MyApp=/home/youruser/workspace/MyApp" \\
    --map "C:\\Users\\you\\.claude\\projects\\e--projects-MyApp=/home/youruser/.claude/projects/-home-youruser-workspace-MyApp"
"""

import argparse
import json
import pathlib
import shutil
import sys


def rewrite_strings(obj, mappings, counts, suppress_pc_entrypoint=False):
    """Recursively walk a JSON-loaded object; rewrite any string value that
    begins with a `from` prefix to the matching `to` prefix, normalising
    remaining backslashes to forward slashes (POSIX).

    When `suppress_pc_entrypoint=True`, ALSO rewrite any dict that has
    `{"entrypoint": "claude-vscode"}` to `{"entrypoint": "cli"}`. This breaks
    the ModeWatcher B->A auto-resume cascade for a migrated session: without
    it, the last record's `claude-vscode` entrypoint makes
    `SessionOwnershipRegistry.Derive` classify owner=Pc; with no ide-lock on
    VPS the predicate "PC provably gone >=45s" fires and ModeWatcher spawns
    `claude --resume <uid>` for a session you only meant to migrate as a
    snapshot. Live caught 2026-05-20.

    Mutates `counts` in place; returns the rewritten object."""
    if isinstance(obj, str):
        for src, dst in mappings:
            if obj.startswith(src):
                tail = obj[len(src):].replace('\\', '/')
                counts[src] = counts.get(src, 0) + 1
                return dst + tail
        return obj
    if isinstance(obj, list):
        return [rewrite_strings(x, mappings, counts, suppress_pc_entrypoint) for x in obj]
    if isinstance(obj, dict):
        new = {k: rewrite_strings(v, mappings, counts, suppress_pc_entrypoint)
               for k, v in obj.items()}
        if suppress_pc_entrypoint and new.get('entrypoint') == 'claude-vscode':
            new['entrypoint'] = 'cli'
            counts['__entrypoint__'] = counts.get('__entrypoint__', 0) + 1
        return new
    return obj


def rewrite_jsonl(path: pathlib.Path, mappings, suppress_pc_entrypoint=False):
    bak = path.with_suffix(path.suffix + '.pre-rewrite.bak')
    if not bak.exists():
        shutil.copy2(path, bak)
    counts = {}
    out_lines = []
    with path.open('r', encoding='utf-8') as f:
        for raw in f:
            line = raw.rstrip('\n')
            if not line:
                out_lines.append('')
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError as e:
                print(f'WARN {path}: skipping unparseable line: {e}', file=sys.stderr)
                out_lines.append(line)
                continue
            new_obj = rewrite_strings(obj, mappings, counts, suppress_pc_entrypoint)
            out_lines.append(json.dumps(new_obj, ensure_ascii=False, separators=(',', ':')))
    with path.open('w', encoding='utf-8') as f:
        f.write('\n'.join(out_lines))
        if out_lines and out_lines[-1] != '':
            f.write('\n')
    return counts


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('target', help='JSONL file or directory containing *.jsonl')
    ap.add_argument('--map', action='append', required=True, metavar='FROM=TO',
                    help='Rewrite mapping (repeatable). FROM is matched as a '
                         'string-value prefix; TO replaces it; trailing '
                         'backslashes in the tail are normalised to /.')
    ap.add_argument('--suppress-claude-vscode-entrypoint', action='store_true',
                    help='Rewrite every record\'s "entrypoint":"claude-vscode" '
                         'to "cli". Strongly recommended for full PC->VPS '
                         'migrations: without it, the last record\'s '
                         'claude-vscode entrypoint makes ModeWatcher classify '
                         'owner=Pc, and with no ide-lock on VPS it auto B->A '
                         'spawns `claude --resume` for a session you only '
                         'meant to migrate as a snapshot.')
    args = ap.parse_args()

    mappings = []
    for m in args.map:
        if '=' not in m:
            print(f'ERROR: --map must be FROM=TO, got: {m!r}', file=sys.stderr)
            sys.exit(2)
        src, dst = m.split('=', 1)
        mappings.append((src, dst))
    # Sort longest src first so a long prefix is preferred over a shorter
    # overlapping one (e.g. C:\Users\... before C:\).
    mappings.sort(key=lambda kv: len(kv[0]), reverse=True)

    target = pathlib.Path(args.target).expanduser()
    if target.is_dir():
        files = sorted(target.glob('*.jsonl'))
    elif target.is_file():
        files = [target]
    else:
        print(f'ERROR: target not found: {target}', file=sys.stderr)
        sys.exit(66)
    if not files:
        print(f'ERROR: no .jsonl files at {target}', file=sys.stderr)
        sys.exit(66)

    total = {src: 0 for src, _ in mappings}
    if args.suppress_claude_vscode_entrypoint:
        total['__entrypoint__'] = 0
    for f in files:
        counts = rewrite_jsonl(f, mappings, args.suppress_claude_vscode_entrypoint)
        if counts:
            print(f'{f.name}: ' + ', '.join(f'{src!r}={n}' for src, n in counts.items()))
        else:
            print(f'{f.name}: no changes')
        for src, n in counts.items():
            total[src] = total.get(src, 0) + n

    print()
    print(f'TOTAL rewrites: {sum(total.values())} across {len(files)} file(s)')
    for src, _dst in mappings:
        print(f'  {src!r} -> {total.get(src, 0)}')
    if args.suppress_claude_vscode_entrypoint:
        print(f'  entrypoint claude-vscode->cli: {total.get("__entrypoint__", 0)}')
    print(f'\nBackups at: <file>.pre-rewrite.bak (idempotent - only created on first run)')


if __name__ == '__main__':
    main()
