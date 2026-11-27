#!/usr/bin/env python3
"""运行时镜像构建编排脚本：固化 Pandoc → TeX Live 串联构建链（T003）。

串联方式（与各 Dockerfile 头注释一致）：

    已发布 sharelatex/sharelatex:server-pro（Dockerfile-base 产物）
      └─ ① pandoc 层   tools/runtime/pandoc/Dockerfile
                        → sharelatex/sharelatex:server-pro-pandoc
      └─ ② texlive 层  --variant 选择（packages / full / cn）
                        → sharelatex/sharelatex:server-pro[-cn]
      └─ ③ wslc save + gzip -6
                        → runtime/images/sharelatex-server-pro[...].tar.gz
      └─ ④ SHA256 + images-manifest.json 清单

用法示例（在仓库根目录执行）：

    python tools/runtime/build-images.py                        # 精选集合变体（默认）
    python tools/runtime/build-images.py --variant full         # scheme-full 超集
    python tools/runtime/build-images.py --skip-pandoc          # 复用已构建的 pandoc 层
    python tools/runtime/build-images.py --variant cn           # 精简中文变体（见 texlive-cn）
    python tools/runtime/build-images.py --no-save              # 只构建不导出（验证用）
    python tools/runtime/build-images.py --mirror https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet

注意：
  * 构建前置：本机 wslc 镜像库里必须已有基础镜像 sharelatex/sharelatex:server-pro
    （发布包 runtime/images 下的 tar.gz 经 `wslc load` 导入即可）。
  * 产物文件名不得包含 ".tar" 之外的句点组合（DeployAsync 按 *.tar* 全量加载，
    清单/sha256 文件名已避开该通配）。
  * pandoc 层的构建上下文需要 vendor/ 静态包，脚本第 ① 步会自动调用
    fetch-pandoc.py（宿主机联网下载，容器内到不了 GitHub）。
"""
from __future__ import annotations

import argparse
import datetime as _dt
import gzip
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
PANDOC_DIR = REPO_ROOT / "tools" / "runtime" / "pandoc"

FINAL_TAG = "sharelatex/sharelatex:server-pro"
PANDOC_STAGE_TAG = "sharelatex/sharelatex:server-pro-pandoc"

# 变体注册表：T011 的 cn 变体在此追加即可，无需改动编排逻辑。
VARIANTS: dict[str, dict] = {
    # 精选集合（与上游 Dockerfile-base 固化列表等价），默认发布形态
    "packages": {
        "dockerfile": "tools/runtime/texlive-packages/Dockerfile",
        "context": "tools/runtime/texlive-packages",
        "tag": FINAL_TAG,
        "filename": "sharelatex-server-pro",
    },
    # scheme-full 超集（约 7-8GB texlive，构建约 1 小时）
    "full": {
        "dockerfile": "tools/runtime/texlive-full/Dockerfile",
        "context": "tools/runtime/texlive-full",
        "tag": FINAL_TAG,
        "filename": "sharelatex-server-pro",
    },
    # 精简「中文常用版」变体（US3/T011）：tlmgr 裁剪 + FROM scratch 单层 squash，
    # 构建内置「保留集 kpsewhich 校验 + 典型中文文档编译」双验收门；
    # gzip -9 保证镜像下载体积 ≤ 2GB（SC-003）。
    "cn": {
        "dockerfile": "tools/runtime/texlive-cn/Dockerfile",
        "context": "tools/runtime/texlive-cn",
        "tag": "sharelatex/sharelatex:server-pro-cn",
        "filename": "sharelatex-server-pro-cn",
        "gzip_level": 9,
    },
}

BUILD_TIMEOUT_SECONDS = 2 * 60 * 60  # scheme-full 构建可达 1 小时，留足余量
SAVE_TIMEOUT_SECONDS = 30 * 60


def run(cmd: list[str], timeout: int | None = None, check: bool = True) -> subprocess.CompletedProcess:
    """执行子进程，实时透传输出，失败时中止。"""
    print(f"$ {' '.join(cmd)}", flush=True)
    proc = subprocess.run(cmd, timeout=timeout, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.stdout:
        print(proc.stdout, end="", flush=True)
    if proc.stderr:
        print(proc.stderr, end="", file=sys.stderr, flush=True)
    if check and proc.returncode != 0:
        print(f"命令失败（exit {proc.returncode}）：{' '.join(cmd)}", file=sys.stderr)
        raise SystemExit(1)
    return proc


def wslc(args: list[str], timeout: int | None = None) -> subprocess.CompletedProcess:
    return run(["wslc", *args], timeout=timeout)


def image_exists(tag: str) -> bool:
    """通过 `wslc images --format json` 判断本地是否已有指定 tag。"""
    proc = wslc(["images", "--format", "json"], timeout=60)
    try:
        entries = json.loads(proc.stdout or "[]")
    except json.JSONDecodeError:
        entries = []
    # wslc 输出为镜像对象数组（Names 含 "repo:tag"），做宽松匹配
    for entry in entries if isinstance(entries, list) else []:
        names = entry.get("Names") or entry.get("names") or []
        if isinstance(names, str):
            names = [names]
        if any(tag in str(n) for n in names):
            return True
        repo = str(entry.get("Repository") or entry.get("repository") or "")
        tg = str(entry.get("Tag") or entry.get("tag") or "")
        if repo and tg and f"{repo}:{tg}" == tag:
            return True
    return False


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def gzip_file(src: Path, dst: Path, level: int = 6) -> None:
    print(f"压缩 {src.name} → {dst.name}（gzip -{level}）…", flush=True)
    with open(src, "rb") as fin, gzip.open(dst, "wb", compresslevel=level) as fout:
        shutil.copyfileobj(fin, fout, length=1 << 20)


def main() -> int:
    ap = argparse.ArgumentParser(description="Overleaf 运行时镜像串联构建（pandoc + texlive 变体）")
    ap.add_argument("--variant", choices=sorted(VARIANTS), default="packages",
                    help="texlive 层变体，默认 packages（精选集合）")
    ap.add_argument("--skip-pandoc", action="store_true",
                    help="跳过 fetch-pandoc 与 pandoc 层构建（复用已有 %s）" % PANDOC_STAGE_TAG)
    ap.add_argument("--skip-fetch", action="store_true",
                    help="跳过 fetch-pandoc.py 下载（vendor/ 已就绪时）")
    ap.add_argument("--pandoc-version", default="3.9")
    ap.add_argument("--pandoc-mirror", default=None,
                    help="GitHub 下载前缀代理（如 https://ghfast.top/https://github.com/jgm/pandoc/releases/download）")
    ap.add_argument("--mirror", default=None,
                    help="CTAN 镜像（TEXLIVE_MIRROR build-arg，默认用 Dockerfile 内置值）")
    ap.add_argument("--out-dir", default=str(REPO_ROOT / "runtime" / "images"),
                    help="tar.gz 导出目录，默认 runtime/images")
    ap.add_argument("--no-save", action="store_true", help="只构建不导出（验证用）")
    args = ap.parse_args()

    variant = VARIANTS[args.variant]
    out_dir = Path(args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    dockerfile = REPO_ROOT / variant["dockerfile"]
    context = REPO_ROOT / variant["context"]

    # ── 前置检查 ────────────────────────────────────────────────
    if not shutil.which("wslc"):
        print("未找到 wslc：请先安装 WSL 3.0+ 容器工具", file=sys.stderr)
        return 2
    if not dockerfile.exists():
        print(f"未找到 Dockerfile：{dockerfile}", file=sys.stderr)
        return 2
    if not image_exists(FINAL_TAG):
        print(f"本地镜像库缺少基础镜像 {FINAL_TAG}，请先导入发布包内的 tar.gz（wslc load）",
              file=sys.stderr)
        return 2

    # ── ① fetch-pandoc + pandoc 层 ─────────────────────────────
    if not args.skip_pandoc:
        fetch_cmd = [sys.executable, str(PANDOC_DIR / "fetch-pandoc.py"),
                     "--version", args.pandoc_version]
        if args.pandoc_mirror:
            fetch_cmd += ["--mirror", args.pandoc_mirror]
        if not args.skip_fetch:
            run(fetch_cmd, timeout=600)

        build_args = ["build", "-f", str(PANDOC_DIR / "Dockerfile"),
                      "-t", PANDOC_STAGE_TAG,
                      "--build-arg", f"PANDOC_VERSION={args.pandoc_version}",
                      str(PANDOC_DIR)]
        wslc(build_args, timeout=BUILD_TIMEOUT_SECONDS)
    elif not image_exists(PANDOC_STAGE_TAG):
        print(f"--skip-pandoc 但本地没有 {PANDOC_STAGE_TAG}", file=sys.stderr)
        return 2

    # ── ② texlive 变体层（基于 pandoc 阶段镜像） ───────────────
    final_tag = variant["tag"]
    build_args = ["build", "-f", str(dockerfile), "-t", final_tag,
                  "--build-arg", f"OVERLEAF_BASE_TAG={PANDOC_STAGE_TAG}",
                  str(context)]
    if args.mirror:
        build_args += ["--build-arg", f"TEXLIVE_MIRROR={args.mirror}"]
    print(f"构建变体 {args.variant} → {final_tag}")
    wslc(build_args, timeout=BUILD_TIMEOUT_SECONDS)

    if args.no_save:
        print("--no-save：构建完成，跳过导出")
        return 0

    # ── ③ wslc save + gzip ─────────────────────────────────────
    tar_name = variant["filename"] + ".tar"
    tgz_name = variant["filename"] + ".tar.gz"
    with tempfile.TemporaryDirectory(prefix="ayaka-build-") as tmp:
        tar_path = Path(tmp) / tar_name
        wslc(["save", "-o", str(tar_path), final_tag], timeout=SAVE_TIMEOUT_SECONDS)
        tgz_path = out_dir / tgz_name
        gzip_file(tar_path, tgz_path, level=int(variant.get("gzip_level", 6)))

    # ── ④ SHA256 + 清单 ────────────────────────────────────────
    digest = sha256_of(tgz_path)
    sha_path = out_dir / (variant["filename"] + ".sha256")
    sha_path.write_text(f"{digest}  {tgz_name}\n", encoding="utf-8")

    manifest_path = out_dir / "images-manifest.json"
    manifest: dict = {}
    if manifest_path.exists():
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            manifest = {}
    manifest[tgz_name] = {
        "variant": args.variant,
        "tag": final_tag,
        "pandocVersion": args.pandoc_version,
        "sizeBytes": tgz_path.stat().st_size,
        "sha256": digest,
        "builtAt": _dt.datetime.now().isoformat(timespec="seconds"),
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                             encoding="utf-8")

    size_mib = tgz_path.stat().st_size / 1048576
    print(f"完成：{tgz_path}（{size_mib:.1f} MiB）")
    print(f"sha256 {digest}")
    print(f"清单已更新：{manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
