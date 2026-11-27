#!/usr/bin/env python3
"""下载 pandoc 官方静态包到构建上下文（宿主机联网，容器内网络到不了 GitHub）。

用法：
    python tools/runtime/pandoc/fetch-pandoc.py                # 默认 3.9 / 自动识别架构
    python tools/runtime/pandoc/fetch-pandoc.py --version 3.9 --mirror https://ghfast.top/https://github.com/jgm/pandoc/releases/download

产物：tools/runtime/pandoc/vendor/pandoc-<ver>-linux-<arch>.tar.gz
该目录已加入 .gitignore（30MB 二进制不入库），Dockerfile 直接 COPY 它。
"""
from __future__ import annotations

import argparse
import hashlib
import platform
import sys
import urllib.request
from pathlib import Path

DEFAULT_MIRROR = "https://github.com/jgm/pandoc/releases/download"
ARCH_MAP = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="3.9")
    ap.add_argument("--arch", default=None, help="amd64 / arm64，默认按本机架构")
    ap.add_argument("--mirror", default=DEFAULT_MIRROR)
    ap.add_argument("--force", action="store_true", help="已存在也重新下载")
    args = ap.parse_args()

    arch = args.arch or ARCH_MAP.get(platform.machine().lower())
    if arch not in ("amd64", "arm64"):
        print(f"不支持的架构：{platform.machine()}", file=sys.stderr)
        return 2

    name = f"pandoc-{args.version}-linux-{arch}.tar.gz"
    url = f"{args.mirror}/{args.version}/{name}"
    out_dir = Path(__file__).resolve().parent / "vendor"
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / name

    if out.exists() and not args.force:
        print(f"已存在：{out}（{out.stat().st_size / 1048576:.1f} MiB），跳过下载")
        return 0

    print(f"下载 {url}")
    tmp = out.with_suffix(".part")
    with urllib.request.urlopen(url, timeout=120) as resp, open(tmp, "wb") as fh:
        total = int(resp.headers.get("Content-Length") or 0)
        done = 0
        while chunk := resp.read(1 << 20):
            fh.write(chunk)
            done += len(chunk)
            if total:
                print(f"\r  {done / 1048576:.1f}/{total / 1048576:.1f} MiB", end="", flush=True)
    print()
    tmp.replace(out)

    digest = hashlib.sha256(out.read_bytes()).hexdigest()
    print(f"完成：{out}")
    print(f"sha256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
