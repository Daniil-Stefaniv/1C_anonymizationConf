#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Dependency-free cleaner for 1C configuration source dumps.

Default mode touches only common 1C text files. The --all-text option is now
conservative: unknown extensions are processed only when the file content looks
like XML. BSL comment removal is applied only to .bsl/.os modules.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable


DEFAULT_EXCLUDED_DIRS = {
    ".git",
    ".agents",
    ".codex",
    "__pycache__",
    "_1c_temp_ib",
    "_ibcmd_data",
    "_chrome_pdf_profile",
    "_report_render",
    "Clean1CCommentsReports",
}

BSL_EXTENSIONS = {".bsl", ".os"}
XML_EXTENSIONS = {".xml", ".xsd", ".xsl", ".xslt"}
XML_TEXT_EXTENSIONS = XML_EXTENSIONS | {".html", ".htm", ".xhtml", ".txt"}
DEFAULT_TEXT_EXTENSIONS = BSL_EXTENSIONS | XML_TEXT_EXTENSIONS
HARD_BINARY_EXTENSIONS = {
    ".png",
    ".jpg",
    ".jpeg",
    ".gif",
    ".bmp",
    ".ico",
    ".zip",
    ".7z",
    ".rar",
    ".pdf",
    ".doc",
    ".docx",
    ".xls",
    ".xlsx",
    ".ppt",
    ".pptx",
    ".epf",
    ".erf",
    ".cf",
    ".cfu",
    ".exe",
    ".dll",
    ".pdb",
}

XML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
COMMENT_FIELD_RE = re.compile(
    r"<(?P<tag>(?:[A-Za-z_][\w.-]*:)?(?:Comment|Комментарий))"
    r"(?P<attrs>(?:\s+[^<>]*?)?)>"
    r"(?P<body>.*?)"
    r"</(?P=tag)>",
    re.DOTALL,
)


class SkipFile(Exception):
    pass


@dataclass
class DecodedText:
    text: str
    encoding: str
    preamble: bytes = b""


@dataclass
class FileStats:
    path: str
    changed: bool = False
    bsl_comments_removed: int = 0
    xml_comments_removed: int = 0
    comment_fields_cleared: int = 0
    skipped: bool = False
    skip_reason: str | None = None
    error: str | None = None


@dataclass
class RunStats:
    root: str
    dry_run: bool
    files_seen: int = 0
    files_processed: int = 0
    files_changed: int = 0
    bsl_comments_removed: int = 0
    xml_comments_removed: int = 0
    comment_fields_cleared: int = 0
    errors: int = 0
    skipped: int = 0


def looks_binary(data: bytes) -> bool:
    signatures = (
        b"PK\x03\x04",
        b"\x89PNG",
        b"\xff\xd8\xff",
        b"GIF8",
        b"BM",
        b"%PDF",
        b"MZ",
    )
    if any(data.startswith(signature) for signature in signatures):
        return True
    return b"\x00" in data[:4096]


def decode_text(data: bytes) -> DecodedText:
    if looks_binary(data):
        raise SkipFile("Пропущен бинарный файл.")

    if data.startswith(b"\xef\xbb\xbf"):
        return DecodedText(data[3:].decode("utf-8"), "utf-8", b"\xef\xbb\xbf")
    if data.startswith(b"\xff\xfe"):
        return DecodedText(data[2:].decode("utf-16-le"), "utf-16-le", b"\xff\xfe")
    if data.startswith(b"\xfe\xff"):
        return DecodedText(data[2:].decode("utf-16-be"), "utf-16-be", b"\xfe\xff")

    try:
        return DecodedText(data.decode("utf-8"), "utf-8")
    except UnicodeDecodeError:
        return DecodedText(data.decode("cp1251"), "cp1251")


def encode_text(decoded: DecodedText, text: str) -> bytes:
    return decoded.preamble + text.encode(decoded.encoding)


def strip_bsl_comments(text: str) -> tuple[str, int]:
    if "//" not in text:
        return text, 0

    result: list[str] = []
    removed = 0
    index = 0
    in_string = False

    while index < len(text):
        char = text[index]

        if in_string:
            result.append(char)
            if char == '"':
                if index + 1 < len(text) and text[index + 1] == '"':
                    result.append(text[index + 1])
                    index += 2
                    continue
                in_string = False
            index += 1
            continue

        if char == '"':
            in_string = True
            result.append(char)
            index += 1
            continue

        if char == "/" and index + 1 < len(text) and text[index + 1] == "/":
            removed += 1
            result.append("// ЗАГЛУШКА")
            index += 2
            while index < len(text) and text[index] not in "\r\n":
                index += 1
            continue

        result.append(char)
        index += 1

    return "".join(result), removed


def clean_xml_like_text(text: str) -> tuple[str, int, int]:
    xml_comments_removed = 0
    if "<!--" in text:
        text, xml_comments_removed = XML_COMMENT_RE.subn("", text)

    comment_fields_cleared = 0
    if "Comment" not in text and "Комментарий" not in text:
        return text, xml_comments_removed, comment_fields_cleared

    def clear_comment_field(match: re.Match[str]) -> str:
        nonlocal comment_fields_cleared
        replacement = f"<{match.group('tag')}{match.group('attrs') or ''}/>"
        if replacement != match.group(0):
            comment_fields_cleared += 1
        return replacement

    text = COMMENT_FIELD_RE.sub(clear_comment_field, text)
    return text, xml_comments_removed, comment_fields_cleared


def looks_like_xml(text: str) -> bool:
    stripped = text.lstrip("\ufeff\r\n\t ")
    return stripped.startswith("<?xml") or stripped.startswith("<MetaDataObject")


def should_parse_as_xml(path: Path, text: str) -> bool:
    if path.suffix.lower() not in XML_EXTENSIONS and not looks_like_xml(text):
        return False
    return text.lstrip("\ufeff\r\n\t ").startswith("<")


def validate_xml(text: str) -> None:
    ET.fromstring(text.lstrip("\ufeff"))


def write_file_atomic(path: Path, data: bytes) -> None:
    fd, temp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent))
    temp_path = Path(temp_name)
    try:
        with os.fdopen(fd, "wb") as file:
            file.write(data)
        os.replace(temp_path, path)
    finally:
        if temp_path.exists():
            temp_path.unlink()


def copy_backup(source: Path, root: Path, backup_dir: Path) -> None:
    target = backup_dir / source.relative_to(root)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def path_is_relative_to(path: Path, base: Path) -> bool:
    try:
        path.relative_to(base)
        return True
    except ValueError:
        return False


def iter_candidate_files(
    root: Path,
    all_text: bool,
    exclude_dirs: set[str],
    exclude_paths: set[Path] | None = None,
) -> Iterable[Path]:
    resolved_exclude_paths = {path.resolve() for path in (exclude_paths or set())}

    for current_root, dirs, files in os.walk(root):
        current = Path(current_root)
        kept_dirs = []
        for name in dirs:
            if name in exclude_dirs:
                continue
            if resolved_exclude_paths:
                child = (current / name).resolve()
                if any(child == excluded or path_is_relative_to(child, excluded) for excluded in resolved_exclude_paths):
                    continue
            kept_dirs.append(name)
        dirs[:] = kept_dirs

        for name in files:
            path = current / name
            if resolved_exclude_paths:
                resolved_path = path.resolve()
                if any(resolved_path == excluded or path_is_relative_to(resolved_path, excluded) for excluded in resolved_exclude_paths):
                    continue

            suffix = path.suffix.lower()
            if suffix in DEFAULT_TEXT_EXTENSIONS:
                yield path
            elif all_text and suffix not in HARD_BINARY_EXTENSIONS:
                yield path


def process_file(
    path: Path,
    root: Path,
    *,
    dry_run: bool,
    backup_dir: Path | None,
    validate_changed_xml: bool,
    validate_dry_run: bool,
    all_text: bool,
) -> FileStats:
    relative = str(path.relative_to(root))
    stats = FileStats(path=relative)
    suffix = path.suffix.lower()

    if all_text and suffix in HARD_BINARY_EXTENSIONS:
        stats.skipped = True
        stats.skip_reason = "Пропущен известный бинарный формат."
        return stats

    try:
        original_bytes = path.read_bytes()
        decoded = decode_text(original_bytes)
    except SkipFile as exc:
        stats.skipped = True
        stats.skip_reason = str(exc)
        return stats
    except (OSError, UnicodeDecodeError) as exc:
        stats.error = f"Не удалось прочитать как текст: {exc}"
        return stats

    is_bsl = suffix in BSL_EXTENSIONS
    is_xml_text = suffix in XML_TEXT_EXTENSIONS
    xml_by_content = looks_like_xml(decoded.text)

    if all_text and not is_bsl and not is_xml_text and not xml_by_content:
        stats.skipped = True
        stats.skip_reason = "Нет признаков XML/BSL-файла выгрузки 1С."
        return stats

    changed_text = decoded.text
    if is_bsl:
        changed_text, stats.bsl_comments_removed = strip_bsl_comments(changed_text)

    if is_xml_text or xml_by_content:
        changed_text, stats.xml_comments_removed, stats.comment_fields_cleared = clean_xml_like_text(changed_text)

    stats.changed = changed_text != decoded.text
    if not stats.changed:
        return stats

    should_validate = validate_changed_xml and (not dry_run or validate_dry_run)
    if should_validate and should_parse_as_xml(path, changed_text):
        try:
            validate_xml(changed_text)
        except ET.ParseError as exc:
            stats.error = f"XML стал невалидным после очистки: {exc}"
            return stats

    if dry_run:
        return stats

    if backup_dir is not None:
        copy_backup(path, root, backup_dir)

    try:
        write_file_atomic(path, encode_text(decoded, changed_text))
    except OSError as exc:
        stats.error = f"Не удалось записать файл: {exc}"

    return stats


def save_report(path: Path, run_stats: RunStats, file_stats: list[FileStats]) -> None:
    payload = {
        "summary": asdict(run_stats),
        "files": [asdict(item) for item in file_stats],
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8-sig")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Replace BSL comments and clean XML comments in a 1C configuration source dump.")
    parser.add_argument("folder", nargs="?", help="Folder with the 1C configuration source dump.")
    parser.add_argument("--apply", action="store_true", help="Actually rewrite files. Default is dry-run.")
    parser.add_argument("--backup-dir", help="Copy original changed files into this folder before rewriting.")
    parser.add_argument("--report", help="JSON report path. Default: Reports next to this script.")
    parser.add_argument("--report-all", action="store_true", help="Include unchanged files in the JSON report.")
    parser.add_argument("--all-text", action="store_true", help="Conservatively inspect unknown text files.")
    parser.add_argument("--exclude-dir", action="append", default=[], help="Directory name to skip. Can be repeated.")
    parser.add_argument("--no-xml-validation", action="store_true", help="Do not parse changed XML files before writing.")
    parser.add_argument("--validate-dry-run", action="store_true", help="Also parse changed XML files during dry-run.")
    parser.add_argument("--jobs", type=int, default=min(8, (os.cpu_count() or 4)), help="Parallel file workers.")
    parser.add_argument("--progress-every", type=int, default=0, help="Print progress every N processed files.")
    parser.add_argument("--self-test", action="store_true", help="Run built-in tests and exit.")
    return parser.parse_args(argv)


def run_self_tests() -> None:
    bsl = (
        'Адрес = "http://example.org/a//b"; // удалить\n'
        'ТекстЗапроса =\n'
        '"ВЫБРАТЬ\n'
        '|////////////////////////////////////////////////////////////////////////////////\n'
        '|ГДЕ Поле = ""A//B"""; // хвост\n'
        '\t// целая строка\n'
        'Возврат Адрес;\n'
    )
    cleaned_bsl, removed = strip_bsl_comments(bsl)
    assert removed == 3
    assert "удалить" not in cleaned_bsl
    assert "хвост" not in cleaned_bsl
    assert "целая строка" not in cleaned_bsl
    assert cleaned_bsl.count("// ЗАГЛУШКА") == 3
    assert cleaned_bsl.count("\n") == bsl.count("\n")
    assert "http://example.org/a//b" in cleaned_bsl
    assert "|////////////////////////////////////////////////////////////////////////////////" in cleaned_bsl
    assert '""A//B""' in cleaned_bsl

    xml = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<MetaDataObject xmlns:xr="urn:test">\n'
        '  <!-- удалить -->\n'
        '  <Comment>Текст</Comment>\n'
        '  <xr:Comment>Текст XR</xr:Comment>\n'
        '  <Name>Объект</Name>\n'
        '</MetaDataObject>\n'
    )
    cleaned_xml, xml_comments, fields = clean_xml_like_text(xml)
    assert xml_comments == 1
    assert fields == 2
    assert "<!--" not in cleaned_xml
    assert "<Comment/>" in cleaned_xml
    assert "<xr:Comment/>" in cleaned_xml
    validate_xml(cleaned_xml)

    decoded = decode_text(b"\xef\xbb\xbf<?xml version=\"1.0\"?><r/>")
    assert not decoded.text.startswith("\ufeff")
    validate_xml(decoded.text)
    assert encode_text(decoded, decoded.text).startswith(b"\xef\xbb\xbf")

    try:
        decode_text(b"PK\x03\x04\x00")
        raise AssertionError("ZIP signature must be skipped as binary")
    except SkipFile:
        pass


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    if args.self_test:
        run_self_tests()
        print("Self-test OK")
        return 0

    if not args.folder:
        print("Error: folder is required unless --self-test is used.", file=sys.stderr)
        return 2

    root = Path(args.folder).resolve()
    if not root.is_dir():
        print(f"Error: folder does not exist: {root}", file=sys.stderr)
        return 2

    dry_run = not args.apply
    exclude_dirs = DEFAULT_EXCLUDED_DIRS | set(args.exclude_dir)
    backup_dir = Path(args.backup_dir).resolve() if args.backup_dir else None
    exclude_paths: set[Path] = set()
    if backup_dir is not None and path_is_relative_to(backup_dir, root):
        exclude_paths.add(backup_dir)

    run_stats = RunStats(root=str(root), dry_run=dry_run)
    details: list[FileStats] = []
    candidates = list(iter_candidate_files(root, args.all_text, exclude_dirs, exclude_paths))
    run_stats.files_seen = len(candidates)

    def process_candidate(candidate: Path) -> FileStats:
        return process_file(
            candidate,
            root,
            dry_run=dry_run,
            backup_dir=backup_dir,
            validate_changed_xml=not args.no_xml_validation,
            validate_dry_run=args.validate_dry_run,
            all_text=args.all_text,
        )

    workers = max(1, args.jobs)
    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = [executor.submit(process_candidate, path) for path in candidates]
        for index, future in enumerate(as_completed(futures), 1):
            file_stats = future.result()
            if args.report_all or file_stats.changed or file_stats.skipped or file_stats.error:
                details.append(file_stats)

            if file_stats.skipped:
                run_stats.skipped += 1
            else:
                run_stats.files_processed += 1

            if file_stats.error:
                run_stats.errors += 1

            if file_stats.changed and not file_stats.error:
                run_stats.files_changed += 1

            run_stats.bsl_comments_removed += file_stats.bsl_comments_removed
            run_stats.xml_comments_removed += file_stats.xml_comments_removed
            run_stats.comment_fields_cleared += file_stats.comment_fields_cleared

            if args.progress_every and index % args.progress_every == 0:
                print(f"Processed {index}/{run_stats.files_seen} files...", flush=True)

    if args.report:
        report_path = Path(args.report).resolve()
    else:
        reports_dir = Path(__file__).resolve().parent / "Reports"
        safe_root_name = root.name or "configuration"
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        report_path = reports_dir / f"{safe_root_name}_clean_report_{stamp}.json"
    save_report(report_path, run_stats, details)

    mode = "DRY-RUN" if dry_run else "APPLY"
    print(f"Mode: {mode}")
    print(f"Root: {root}")
    print(f"Files seen: {run_stats.files_seen}")
    print(f"Files processed: {run_stats.files_processed}")
    print(f"Files changed: {run_stats.files_changed}")
    print(f"BSL comments replaced: {run_stats.bsl_comments_removed}")
    print(f"XML comments removed: {run_stats.xml_comments_removed}")
    print(f"Comment fields cleared: {run_stats.comment_fields_cleared}")
    print(f"Skipped: {run_stats.skipped}")
    print(f"Errors: {run_stats.errors}")
    print(f"Report: {report_path}")

    if dry_run:
        print("No files were rewritten. Re-run with --apply to write changes.")
    elif backup_dir is not None:
        print(f"Backup: {backup_dir}")

    return 1 if run_stats.errors else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
