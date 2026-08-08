#!/usr/bin/env python3
"""Reproduce the AK.21/AK.22 offline record-layer checks.

Validated artifacts (2026-08-06):
  md_full.bin  809333cb66fb64622e7af9f5f1d32836cbca3b19ba66f7c0c41d34caa0a62284
  methods.csv  49aa99ed2e1223577905e46b86902b0e194cf7fdb74dc93204166bbe14a3743a
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import struct
from pathlib import Path
from typing import Any

HEADER_SIZE = 12


def number(value: str) -> int:
    text = value.strip().replace("_", "")
    if text.lower().startswith(("0x", "+0x", "-0x")):
        return int(text, 16)
    try:
        return int(text, 10)
    except ValueError:
        return int(text, 16)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def parse_header(data: bytes, offset: int) -> dict[str, int]:
    if offset < 0 or offset + HEADER_SIZE > len(data):
        raise ValueError(f"record header outside md_full.bin: {offset:#x}")
    item_bytes, eh_count, item_count, eh_size = struct.unpack_from("<HHHH", data, offset + 4)
    return {
        "recOff": offset,
        "maxStack": data[offset],
        "codeSize": int.from_bytes(data[offset + 1 : offset + 4], "little"),
        "itemBytes": item_bytes,
        "ehCount": eh_count,
        "itemCount": item_count,
        "ehDataSize": eh_size,
    }


def binary_control(path: Path, role: str) -> dict[str, Any]:
    data = path.read_bytes()
    anchor = data[:16]
    out: dict[str, Any] = {
        "path": str(path),
        "size": len(data),
        "size_hex": f"0x{len(data):X}",
        "sha256": sha256(path),
        "first16_hex": anchor.hex(),
        "first16_first_hit": data.find(anchor) if anchor else None,
        "first16_count": data.count(anchor) if anchor else 0,
    }
    if role == "host":
        pe = int.from_bytes(data[0x3C:0x40], "little") if len(data) >= 0x40 else -1
        out.update(mz_at_0=data[:2] == b"MZ", pe_offset=pe,
                   pe_signature_ok=0 <= pe <= len(data) - 4 and data[pe:pe + 4] == b"PE\0\0")
    if role == "s2":
        words = len(data) // 4
        tagged = sum(((struct.unpack_from("<I", data, i * 4)[0] >> 24) & 0xF) == 0xA for i in range(words))
        out.update(dword_count=words, tag_nibble_A_count=tagged,
                   tag_nibble_A_all=tagged == words and len(data) % 4 == 0)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--md", type=Path, required=True)
    ap.add_argument("--csv", type=Path, required=True)
    ap.add_argument("--payload", type=Path)
    ap.add_argument("--s2", type=Path)
    ap.add_argument("--host", type=Path)
    ap.add_argument("--s0-size", default="0x57C08")
    ap.add_argument("--out", type=Path, default=Path("ak21_offline_validation.json"))
    args = ap.parse_args()

    md = args.md.read_bytes()
    with args.csv.open("r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))

    required = {"recOff", "maxStack", "codeSize", "nLocals", "ehCount", "ilOffset"}
    if not rows or not required.issubset(rows[0]):
        raise SystemExit(f"methods.csv missing columns: {sorted(required - set(rows[0] if rows else []))}")

    checks = {
        "csv_maxStack_vs_raw": [],
        "csv_codeSize_vs_raw": [],
        "csv_nLocals_vs_raw_itemCount": [],
        "csv_ehCount_vs_raw": [],
        "raw_itemBytes_vs_4_itemCount": [],
        "record_gap_vs_raw_size": [],
        "ilOffset_recurrence": [],
        "record_bounds": [],
    }
    headers: list[dict[str, int] | None] = []

    def mismatch(name: str, row: int, expected: Any, actual: Any) -> None:
        checks[name].append({"row": row, "expected": expected, "actual": actual})

    for i, row in enumerate(rows):
        try:
            h = parse_header(md, number(row["recOff"]))
            headers.append(h)
        except Exception as exc:
            headers.append(None)
            mismatch("record_bounds", i, "valid 12-byte header", str(exc))
            continue
        for csv_name, raw_name, check_name in (
            ("maxStack", "maxStack", "csv_maxStack_vs_raw"),
            ("codeSize", "codeSize", "csv_codeSize_vs_raw"),
            ("nLocals", "itemCount", "csv_nLocals_vs_raw_itemCount"),
            ("ehCount", "ehCount", "csv_ehCount_vs_raw"),
        ):
            actual, expected = number(row[csv_name]), h[raw_name]
            if actual != expected:
                mismatch(check_name, i, expected, actual)
        if h["itemBytes"] != 4 * h["itemCount"]:
            mismatch("raw_itemBytes_vs_4_itemCount", i, 4 * h["itemCount"], h["itemBytes"])

    for i in range(len(rows) - 1):
        a, b = headers[i], headers[i + 1]
        if a and b:
            expected = HEADER_SIZE + a["itemBytes"] + a["ehDataSize"]
            actual = b["recOff"] - a["recOff"]
            if actual != expected:
                mismatch("record_gap_vs_raw_size", i, expected, actual)
        expected_il = number(rows[i]["ilOffset"]) + number(rows[i]["codeSize"])
        actual_il = number(rows[i + 1]["ilOffset"])
        if actual_il != expected_il:
            mismatch("ilOffset_recurrence", i, expected_il, actual_il)

    valid = [h for h in headers if h]
    last_end = (valid[-1]["recOff"] + HEADER_SIZE + valid[-1]["itemBytes"] + valid[-1]["ehDataSize"]) if valid else None
    s0_size = number(args.s0_size)
    artifacts: dict[str, Any] = {
        "md_full.bin": binary_control(args.md, "md"),
        "methods.csv": {"path": str(args.csv), "size": args.csv.stat().st_size, "sha256": sha256(args.csv)},
    }
    for name, path, role in (("pl_full.bin", args.payload, "payload"), ("s2.bin", args.s2, "s2"),
                             ("LordsMobileBot.exe", args.host, "host")):
        if path:
            artifacts[name] = binary_control(path, role)

    report = {
        "row_count": len(rows),
        "md_size": len(md),
        "expected_s0_size": s0_size,
        "last_record_end": last_end,
        "s0_coverage_match": last_end == s0_size,
        "mismatch_counts": {k: len(v) for k, v in checks.items()},
        "mismatches": checks,
        "artifacts": artifacts,
    }
    args.out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"rows={len(rows)} lastRecordEnd={last_end:#x} S0match={last_end == s0_size}")
    for name, values in checks.items():
        print(f"{name}: {len(values)}")
    print(f"report={args.out}")
    return 0 if last_end == s0_size and all(not values for values in checks.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
