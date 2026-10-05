#!/usr/bin/env python3
"""Gitignore oracle: measures git itself and emits the rows GitignoreOracleTests asserts.

Every answer in oracle-table.md / oracle-table.json comes from `git check-ignore -v` run against a
materialized scratch repository, never from the squeezr matcher. The C# theory rows in
tests/Squeezr.Tests/Indexing/Ignore/ are copied out of this output; when a dialect question is
raised, re-run this script rather than arguing from the code.

    python3 oracle-table.py [--work DIR] [--keep-work] [--format cs|md|json|all]

The two evidence files (oracle-table.md, oracle-table.json) are always rewritten next to this
script — they are the committed baseline. --format picks which renderings additionally print to
stdout (default all: the cs and md previews). The scratch repo lives in a temp directory by
default (--work overrides it, --keep-work leaves it behind for inspection); nothing else is
written into the repository. Requires git >= 2.x with core.ignorecase=false available (the
script pins it).
"""

import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

BASE = pathlib.Path(__file__).resolve().parent

# (gitignore body, path, is_dir) — one body per case directory, so no row influences another.
ROWS = [
    # patterns the real .gitignore files in this tree actually carry
    ("[Dd]ebug/", "Debug", True),
    ("[Dd]ebug/", "Debug/app.dll", False),
    ("[Dd]ebug/", "debug/app.dll", False),
    ("[Dd]ebug/", "src/Debug/app.dll", False),
    ("[Dd]ebug/", "Debugx/app.dll", False),
    ("[Dd]ebug/", "DEBUG/app.dll", False),
    ("[Dd]ebugPublic/", "DebugPublic/app.cs", False),
    ("[Dd]ebugPublic/", "Debug/app.cs", False),
    ("[Rr]elease/", "Release/app.dll", False),
    ("[Rr]elease/", "release/app.dll", False),
    ("[Rr]elease/", "Releases/app.dll", False),
    ("[Rr]eleases/", "Releases/app.dll", False),
    ("[Rr]eleases/", "Release/app.dll", False),
    ("[Ww][Ii][Nn]32/", "Win32/app.exe", False),
    ("[Ww][Ii][Nn]32/", "wIn32/app.exe", False),
    ("[Ww][Ii][Nn]32/", "Win64/app.exe", False),
    ("[Aa][Rr][Mm]/", "ARM/app.dll", False),
    ("[Aa][Rr][Mm]/", "ARM64/app.dll", False),
    ("[Aa][Rr][Mm]64/", "ARM64/app.dll", False),
    ("[Aa][Rr][Mm]64/", "ARM/app.dll", False),
    ("[Bb]in/", "bin/app.dll", False),
    ("[Bb]in/", "src/Bin/app.dll", False),
    ("[Bb]in/", "Bin/Debug/app.dll", False),
    ("[Bb]in/", "binx/app.dll", False),
    ("[Oo]bj/", "obj/project.assets.json", False),
    ("[Oo]bj/", "object/keep.cs", False),
    ("[Ll]og/", "log/app.txt", False),
    ("[Ll]og/", "Log", True),
    ("[Ll]og/", "Logs/app.txt", False),
    ("[Ll]ogs/", "Logs/app.txt", False),
    ("[Ll]ogs/", "Log/app.txt", False),
    ("Generated\\ Files/", "Generated Files/model.cs", False),
    ("Generated\\ Files/", "src/Generated Files/nested/model.cs", False),
    ("Generated\\ Files/", "GeneratedFiles/model.cs", False),
    ("Generated\\ Files/", "Generated  Files/model.cs", False),
    ("[Tt]est[Rr]esult*/", "TestResults/run.trx", False),
    ("[Tt]est[Rr]esult*/", "testResultZ/run.trx", False),
    ("[Tt]est[Rr]esult*/", "TestResult/run.trx", False),
    ("[Tt]est[Rr]esult*/", "Tests/run.trx", False),
    ("[Bb]uild[Ll]og.*", "BuildLog.txt", False),
    ("[Bb]uild[Ll]og.*", "buildlog.htm", False),
    ("[Bb]uild[Ll]og.*", "BuildLog", False),
    ("[Bb]uild[Ll]og.*", "BuildLogs.txt", False),
    ("[Dd]ebugPS/", "DebugPS/app.dll", False),
    ("[Dd]ebugPS/", "Debug/app.dll", False),
    ("[Rr]eleasePS/", "ReleasePS/app.dll", False),
    ("[Rr]eleasePS/", "Release/app.dll", False),
    ("*.[Rr]e[Ss]harper", "foo.reSharper", False),
    ("*.[Rr]e[Ss]harper", ".ReSharper", False),
    ("*.[Rr]e[Ss]harper", "src/project.resharper", False),
    ("*.[Rr]e[Ss]harper", "foo.resharperx", False),
    ("*.[Rr]e[Ss]harper", "foo.xReSharper", False),
    ("[Ee]xpress/", "Express/app.txt", False),
    ("[Ee]xpress/", "expresso/app.txt", False),
    ("*.[Pp]ublish.xml", "Web.Publish.xml", False),
    ("*.[Pp]ublish.xml", "web.publish.xmll", False),
    ("**/[Pp]ackages/*", "Packages/newtonsoft.json.dll", False),
    ("**/[Pp]ackages/*", "src/Packages/a/b.dll", False),
    ("**/[Pp]ackages/*", "packagesx/keep.dll", False),
    ("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/build/keep.dll", False),
    ("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/build", True),
    ("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/a.dll", False),
    ("*.[Cc]ache", "session.cache", False),
    ("*.[Cc]ache", "src/token.Cache", False),
    ("*.[Cc]ache", "session.cache2", False),
    ("*.[Cc]ache", "FOO.CACHE", False),
    ("*.[Cc]ache", "cache", False),
    ("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache", False),
    ("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache", True),
    ("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache/inner.txt", False),
    ("*- [Bb]ackup.rdl", "report - Backup.rdl", False),
    ("*- [Bb]ackup.rdl", "report - backup.rdl", False),
    ("*- [Bb]ackup.rdl", "report -Backup.rdl", False),
    ("*- [Bb]ackup ([0-9]).rdl", "report - Backup (1).rdl", False),
    ("*- [Bb]ackup ([0-9]).rdl", "report - Backup (12).rdl", False),
    ("*- [Bb]ackup ([0-9]).rdl", "report - Backup (a).rdl", False),
    ("*- [Bb]ackup ([0-9][0-9]).rdl", "report - Backup (12).rdl", False),
    ("*- [Bb]ackup ([0-9][0-9]).rdl", "report - Backup (1).rdl", False),
    ("[Dd]esktop.ini", "Desktop.ini", False),
    ("[Dd]esktop.ini", "src/Desktop.ini", False),
    ("[Dd]esktop.ini", "desktop.ini", False),
    ("[Dd]esktop.ini", "Thumbs.db.ini", False),
    # structural shapes of the dialect the matcher claims
    ("[a-c]x", "bx", False),
    ("[a-c]x", "dx", False),
    ("[!x]y", "zy", False),
    ("[!x]y", "xy", False),
    ("[^x]y", "zy", False),
    ("[^x]y", "xy", False),
    ("[]a]z", "]z", False),
    ("[]a]z", "az", False),
    ("[]a]z", "bz", False),
    ("[a-]b", "-b", False),
    ("[a-]b", "ab", False),
    ("[a-]b", "bb", False),
    ("[--0]Q", ".Q", False),
    ("[--0]Q", "-Q", False),
    ("[--0]Q", "0Q", False),
    ("[--0]Q", "aQ", False),
    ("[.*]Q", ".Q", False),
    ("[.*]Q", "*Q", False),
    ("[.*]Q", "aQ", False),
    ("[a^]b", "ab", False),
    ("[a^]b", "^b", False),
    ("[!^a]b", "ab", False),
    ("[!^a]b", "^b", False),
    ("[!^a]b", "bb", False),
    ("[\\[-\\]]b", "[b", False),
    ("[\\[-\\]]b", "]b", False),
    ("[\\[-\\]]b", "ab", False),
    ("[z-a]b", "zb", False),
    ("[z-a]b", "ab", False),
    ("[z-a]b", "-b", False),
    ("[z-a]b", "{b", False),
    ("[9-1]Q", "9Q", False),
    ("[9-1]Q", "1Q", False),
    ("[9-1]Q", "5Q", False),
    ("[A-Z]*.cs", "Foo.cs", False),
    ("[A-Z]*.cs", "foo.cs", False),
    ("[a]Q", "aQ", False),
    ("a\\*b", "a*b", False),
    ("a\\*b", "axb", False),
    ("a\\[b", "a[b", False),
    ("a\\\\b", "a\\b", False),
    ("a\\?b", "a?b", False),
    ("\\#hash", "#hash", False),
    ("\\!bang", "!bang", False),
    ("Generated\\ Files/x", "Generated Files/x", False),
    ("Generated\\ Files/x", "Generated Files/y", False),
    ("Generated\\ Files/x", "Generated Files/x/y", False),
    ("foo\\", "foo\\", False),
    ("foo\\", "foo", False),
    ("foo\\ ", "foo ", False),
    ("foo\\ ", "foo", False),
    ("[bracket", "[bracket", False),
    ("[bracket", "b", False),
    ("x[[y", "x[y", False),
    ("x[[y", "xy", False),
    ("[[:alpha:]]", "b", False),
    ("[[:alpha:]]", "a]", False),
    ("[[:alpha:]]", "1", False),
    ("[[:digit:]]", "5", False),
    ("[a[:digit:]b]Q", "aQ", False),
    ("[a[:digit:]b]Q", "bQ", False),
    ("[a[:digit:]b]Q", "5Q", False),
    ("[\\]", "]", False),
    ("[\\]", "\\", False),
    ("[\\\\]", "\\", False),
    ("[\\\\]", "]", False),
    ("a\\/b", "a/b", False),
    ("a\\/b", "ab", False),
    ("[a/]x", "ax", False),
    ("[a/]x", "bracket/ax", False),
    ("[!a/]z", "bz", False),
    ("[.-0]x", ".x", False),
    ("[.-0]x", "0x", False),
    ("[.-0]x", "-x", False),
    ("[.-/]x", ".x", False),
    ("[[]", "[", False),
    ("[[]", "a", False),
]


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--work", default=None, help="scratch directory for the probe repositories")
    ap.add_argument("--keep-work", action="store_true", help="leave the scratch tree behind")
    ap.add_argument("--format", choices=["cs", "md", "all"], default="all",
                    help="stdout preview rendering; the md/json evidence files are always rewritten")
    return ap.parse_args()


def init_scratch(work: pathlib.Path):
    work.mkdir(parents=True, exist_ok=True)
    if not (work / ".git").exists():
        subprocess.run(["git", "init", "-q", "."], cwd=work, check=True)
    subprocess.run(["git", "config", "core.ignorecase", "false"], cwd=work, check=True)


def git_ignores(work: pathlib.Path, body: str, path: str, is_dir: bool, case_dir: str):
    root = work / case_dir
    target = root / path
    if is_dir:
        target.mkdir(parents=True, exist_ok=True)
        (target / "payload").write_text("x")
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("x")

    (root / ".gitignore").write_text(body + "\n")

    out = subprocess.run(
        ["git", "check-ignore", "-v", "--", str(target)],
        cwd=work, capture_output=True, text=True)
    if out.returncode not in (0, 1):
        raise RuntimeError(f"git check-ignore failed: {out.stderr}")
    if out.returncode == 1:
        return False, ""
    line = out.stdout.split("\t", 1)[0]
    matched = line.split(":", 2)[2]
    return (not matched.startswith("!")), matched


def sanity_check(rows):
    # An all-False table means git never saw the case paths (a relative --work is the classic way to
    # get one), not that the dialect stopped matching.
    if not any(r["git"] for r in rows):
        raise RuntimeError("git answered False to every row — the scratch tree is not readable, check --work")


def measure(work: pathlib.Path):
    rows = []
    for index, (body, path, is_dir) in enumerate(ROWS):
        ignored, matched = git_ignores(work, body, path, is_dir, f"case-{index:03d}")
        rows.append({"body": body, "path": path, "isDir": is_dir, "git": ignored, "via": matched})
    sanity_check(rows)
    return rows


def emit_cs(rows):
    for r in rows:
        print(f'    [InlineData({json.dumps(r["body"])}, {json.dumps(r["path"])}, '
              f'{"true" if r["isDir"] else "false"}, {"true" if r["git"] else "false"})]')


def emit_md(rows):
    print("# evidence table")
    for r in rows:
        print(f'{str(r["git"]):<6} body={r["body"]!r:<40} path={r["path"]!r:<28} '
              f'dir={r["isDir"]!s:<6} via={r["via"]!r}')


def main():
    args = parse_args()
    made_temp = args.work is None
    work = pathlib.Path(args.work).resolve() if args.work else pathlib.Path(tempfile.mkdtemp(prefix="squeezr-oracle-"))
    try:
        init_scratch(work)
        rows = measure(work)
    finally:
        if made_temp and not args.keep_work:
            shutil.rmtree(work, ignore_errors=True)

    (BASE / "oracle-table.json").write_text(json.dumps(rows, indent=2) + "\n")
    md = ["<!-- generated by tests/oracle/gitignore/oracle-table.py — answers from `git check-ignore -v`,"
          " do not hand-edit -->", ""]
    for r in rows:
        md.append(f'{str(r["git"]):<6} body={r["body"]!r:<40} path={r["path"]!r:<28} '
                  f'dir={r["isDir"]!s:<6} via={r["via"]!r}')
    (BASE / "oracle-table.md").write_text("\n".join(md) + "\n")

    if args.format in ("cs", "all"):
        print(f"// generated by tests/oracle/gitignore/oracle-table.py"
              f" — {len(rows)} rows measured by `git check-ignore -v`")
        emit_cs(rows)
    if args.format in ("md", "all"):
        print()
        emit_md(rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
