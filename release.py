#!/usr/bin/env python3
r"""AyakaLauncher 发布打包脚本（release.ps1 的 Python 版，功能与参数对齐）。

默认产出：
  release/AyakaLauncher_<配置>_<平台>.exe   —— 可执行文件（命名与 Release 附件对齐）
  release/runtime.zip                       —— 内嵌运行时（约 4.2GB，含 4 个镜像包：
                                              sharelatex-server-pro / git-bridge /
                                              mongo-8.0 / redis-7；主镜像超过 GitHub
                                              普通仓库 100MB 单文件限制，故不入库）
Release 不再附带运行时包；对外分发统一使用网盘提供的 runtime.7z，
本机生成的 release/runtime.zip 仅供整合包与离线分发，不上传到 Release。

可选产出（--with-bundle）：
  release/AyakaLauncher_<版本>_<配置>_<平台>.zip —— exe + runtime 的整合包（约 4.2GB）

本机运行目录：dist\ 存在时（本机日常运行布局），发布后自动同步新 exe 到 dist\AyakaLauncher.exe；
exe 被占用（启动器正在运行）时只告警不失败。

输出行为：dotnet publish 仅在失败时输出 MSBuild 日志，成功时保持安静；
打包 runtime.zip / 整合包时显示字节级进度条。

目标平台默认 auto：自动探测本机处理器架构（x86_64/AMD64 → x64，
ARM64/AArch64 → arm64），显式指定 --platform x64/arm64 可覆盖；
runtime.zip 与 CPU 架构无关，任意目标平台均会产出（通用运行时包）。

用法：python release.py [--configuration Release] [--platform auto] [--with-bundle]
      python release.py --bump [--version X.Y.Z] [--code N]     # 版本号同步升级
      python release.py --upload [--platform x64[,arm64]] [--tag vX.Y.Z]
                               [--release-title 标题] [--notes-file 文件] [--prerelease]
                                                                # 构建并上传 GitHub Release
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ARTIFACT = ROOT / "artifact"
RELEASE_DIR = ROOT / "release"
RUNTIME_DIR = ROOT / "runtime"
RUNTIME_IMAGES = RUNTIME_DIR / "images"
# git-bridge.tar.gz 为 Git-Bridge / MCP / 三方同步的必选镜像（启用对应功能时容器必需）
REQUIRED_IMAGES = ["sharelatex-server-pro.tar.gz", "git-bridge.tar.gz", "mongo-8.0.tar.gz", "redis-7.tar.gz"]
# tar.gz 等已压缩数据用最低压缩级（对已 gzip 内容再压缩几乎无收益，可大幅提速），
# 其余文件用常规压缩级；与 PS 版 Fastest/Optimal 分级一致
FASTEST_SUFFIX = re.compile(r"\.(gz|tgz|zip|7z|rar|png|jpe?g|webp|mp4|vhdx)$", re.IGNORECASE)

PROGRESS_INTERVAL = 0.25  # 进度刷新间隔（秒），与 PS 版一致
BAR_WIDTH = 26            # 进度条宽度（pip 风格字符条）
ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")


def fail(message: str):
    raise SystemExit(message)


USE_COLOR = False  # 真·控制台（tty）时启用 pip 风格彩色进度条


def setup_stdio() -> None:
    """重定向（Git Bash / CI 管道）时输出按 UTF-8，避免中文乱码；交互式 conhost 无需处理。"""
    global USE_COLOR
    for stream in (sys.stdout, sys.stderr):
        if not stream.isatty():
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass
    USE_COLOR = sys.stdout.isatty()
    if USE_COLOR and os.name == "nt":
        # 老 conhost 需要显式开启 VT 转义序列；Windows Terminal 原生支持
        try:
            k32 = ctypes.windll.kernel32
            handle = k32.GetStdHandle(-11)
            mode = ctypes.c_uint32()
            if k32.GetConsoleMode(handle, ctypes.byref(mode)):
                k32.SetConsoleMode(handle, mode.value | 0x0004)
        except Exception:
            pass


def _c(code: str, text: str) -> str:
    return f"\x1b[{code}m{text}\x1b[0m" if USE_COLOR else text


def _fmt_eta(seconds: float) -> str:
    seconds = max(0, int(seconds))
    return f"{seconds // 3600}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


def _render_bar(frac: float) -> str:
    """pip 风格字符条：绿色实心 ━ + 右边界 ╸ + 暗色剩余 ━。"""
    filled = frac * BAR_WIDTH
    full = int(filled)
    boundary = "╸" if full < BAR_WIDTH and filled - full > 0.02 else ""
    rest = BAR_WIDTH - full - len(boundary)
    return _c("32", "━" * full + boundary) + _c("90", "━" * rest)


# 进度状态：速率用指数滑动平均，平滑抖动（与 pip 的观感一致）
_progress = {"last_time": 0.0, "last_bytes": 0, "speed": 0.0}


def print_progress(activity: str, entry: str, done: int, total: int) -> None:
    now = time.monotonic()
    if _progress["last_time"] and now > _progress["last_time"]:
        instant = (done - _progress["last_bytes"]) / (now - _progress["last_time"])
        _progress["speed"] = instant if _progress["speed"] == 0 else _progress["speed"] * 0.7 + instant * 0.3
    _progress["last_time"] = now
    _progress["last_bytes"] = done

    speed = _progress["speed"]
    frac = min(1.0, done / total)
    size_text = f"{done / 1048576:.1f}/{total / 1048576:.1f} MB"

    if USE_COLOR:
        parts = [_c("97", activity), _render_bar(frac), _c("1", size_text)]
        if speed > 0:
            parts.append(_c("36", f"{speed / 1048576:.1f} MB/s"))
            parts.append(_c("95", "eta " + _fmt_eta((total - done) / speed)))
        line = "\r" + " ".join(parts)
    else:
        pct = min(100, int(100.0 * frac))
        line = f"\r{activity}　{entry}　{size_text}（{pct}%）"

    visible_len = len(ANSI_RE.sub("", line))
    line += " " * max(0, print_progress.last_len - visible_len)
    sys.stdout.write(line)
    sys.stdout.flush()
    print_progress.last_len = visible_len


print_progress.last_len = 0


def get_file_version(path: Path) -> str:
    """读 exe 的 FileVersion（如 1.2.3.0 → 1.2.3），与 PS 的 VersionInfo.FileVersion 行为一致。"""
    version_dll = ctypes.WinDLL("version")
    version_dll.GetFileVersionInfoSizeW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p]
    version_dll.GetFileVersionInfoSizeW.restype = ctypes.c_uint32
    version_dll.GetFileVersionInfoW.argtypes = [
        ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    version_dll.GetFileVersionInfoW.restype = ctypes.c_int
    version_dll.VerQueryValueW.argtypes = [
        ctypes.c_void_p, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_uint32)]
    version_dll.VerQueryValueW.restype = ctypes.c_int

    size = version_dll.GetFileVersionInfoSizeW(str(path), None)
    if not size:
        fail(f"读取版本信息失败：{path}")
    data = ctypes.create_string_buffer(size)
    if not version_dll.GetFileVersionInfoW(str(path), 0, size, data):
        fail(f"读取版本信息失败：{path}")
    value = ctypes.c_void_p()
    value_len = ctypes.c_uint32()
    if not version_dll.VerQueryValueW(data, "\\", ctypes.byref(value), ctypes.byref(value_len)) or not value_len.value:
        fail(f"读取版本信息失败：{path}")
    # VS_FIXEDFILEINFO：dwFileVersionMS 在偏移 8（高 16 位.低 16 位），dwFileVersionLS 在偏移 12
    fixed = ctypes.string_at(value.value, 16)
    ms = int.from_bytes(fixed[8:12], "little")
    ls = int.from_bytes(fixed[12:16], "little")
    full = f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"
    return re.sub(r"\.0$", "", full)


def detect_platform() -> tuple[str, str]:
    """探测本机处理器架构，返回 (目标平台, 原始架构名)。

    x86_64 / AMD64 → x64；ARM64 / AArch64 → arm64；32 位 x86 直接报错
    （WSL 3.0 要求 64 位 Windows）。PROCESSOR_ARCHITEW6432 优先——
    WOW64 下它才是 OS 真实架构（x64 进程跑在 ARM64 Windows 模拟层下时同样适用）。
    """
    raw = (os.environ.get("PROCESSOR_ARCHITEW6432")
           or os.environ.get("PROCESSOR_ARCHITECTURE") or "").strip()
    arch = raw.upper()
    if arch in ("AMD64", "X86_64", "X64"):
        return "x64", raw
    if arch in ("ARM64", "AARCH64"):
        return "arm64", raw
    if arch == "X86":
        fail("检测到 32 位 x86 系统；AyakaLauncher 需要 64 位 Windows（x64 / ARM64）。")
    fail(f"无法识别本机处理器架构：{raw or '未知'}；请用 --platform 显式指定 x64 或 arm64。")


def publish(configuration: str, platform: str) -> None:
    """dotnet publish（成功时安静，失败时输出 MSBuild 日志）。"""
    print(f"发布 {configuration}/{platform} …")
    ARTIFACT.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [
            "dotnet", "publish",
            str(ROOT / "Plain Craft Launcher 2" / "Plain Craft Launcher 2.csproj"),
            f"-p:Configuration={configuration}",
            f"-p:Platform={platform}",
            "-p:DeleteExistingFiles=true",
            "-o", str(ARTIFACT),
            "--no-self-contained", "--nologo", "-v", "q", "-clp:ErrorsOnly",
        ],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if result.returncode != 0:
        print(result.stdout, end="")
        if result.stderr:
            print(result.stderr, end="")
        fail(f"dotnet publish 失败（{result.returncode}）")


def runtime_entries() -> list[tuple[Path, str]]:
    """runtime 目录全部文件 → (源路径, zip 内条目名)（前缀 runtime/，与 PS 版一致）。"""
    entries = []
    for path in sorted(RUNTIME_DIR.rglob("*")):
        if path.is_file():
            rel = path.relative_to(RUNTIME_DIR).as_posix()
            entries.append((path, "runtime/" + rel))
    return entries


def compress_entries(zip_path: Path, activity: str, entries: list[tuple[Path, str]]) -> None:
    """流式压缩到 zip，带字节级进度条（条目内按 4MB 块推进）。"""
    zip_path = Path(zip_path)
    if zip_path.exists():
        zip_path.unlink()
    if not entries:
        fail(f"没有可打包的条目：{zip_path}")
    total = sum(os.path.getsize(src) for src, _ in entries) or 1
    done = 0
    last_report = 0.0
    _progress.update(last_time=0.0, last_bytes=0, speed=0.0)
    try:
        with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as zf:
            for src, name in entries:
                level = 1 if FASTEST_SUFFIX.search(name) else 6
                info = zipfile.ZipInfo(name, date_time=time.localtime(os.path.getmtime(src))[:6])
                info.compress_type = zipfile.ZIP_DEFLATED
                info._compresslevel = level  # zipfile 公开 API 不支持 open(w) 按条目设级，此属性为稳定实现细节
                with open(src, "rb") as f, zf.open(info, "w", force_zip64=True) as dst:
                    while chunk := f.read(4 * 1024 * 1024):
                        dst.write(chunk)
                        done += len(chunk)
                        now = time.monotonic()
                        if now - last_report >= PROGRESS_INTERVAL:
                            print_progress(activity, name, done, total)
                            last_report = now
        sys.stdout.write("\r" + " " * print_progress.last_len + "\r")
        sys.stdout.flush()
        print_progress.last_len = 0
    except BaseException:
        # 中断/失败时结束进度行，错误信息另起一行输出
        sys.stdout.write("\n")
        raise


def human_size(path: Path) -> str:
    size = path.stat().st_size
    return f"{size / 1073741824:.2f} GB" if size >= 1073741824 else f"{size / 1048576:.2f} MB"


METADATA_PATH = ROOT / "Plain Craft Launcher 2" / "metadata.json"
CSPROJ_PATH = ROOT / "Plain Craft Launcher 2" / "Plain Craft Launcher 2.csproj"
GITHUB_REPO = "BluesilveEmperor/Ayaka-Leaf-PCL"


def load_metadata() -> dict:
    return json.loads(METADATA_PATH.read_text(encoding="utf-8"))


def bump(version: str | None, code: int | None) -> str:
    """同步更新 metadata.json（base/code）与 csproj（AssemblyVersion/FileVersion）。

    返回新版本号（X.Y.Z）。version 缺省时 minor +1、patch 归零；code 缺省时 +1。
    """
    meta = load_metadata()
    base = meta["version"]["base"]
    if version is None:
        parts = base.split(".")
        parts[1] = str(int(parts[1]) + 1)
        version = ".".join(parts[:2] + ["0"])
    if code is None:
        code = int(meta["version"]["code"]) + 1

    meta["version"]["base"] = version
    meta["version"]["code"] = code
    METADATA_PATH.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    csproj = CSPROJ_PATH.read_text(encoding="utf-8")
    csproj = re.sub(r"<AssemblyVersion>[\d.]+</AssemblyVersion>",
                    f"<AssemblyVersion>{version}.0</AssemblyVersion>", csproj)
    csproj = re.sub(r"<FileVersion>[\d.]+</FileVersion>",
                    f"<FileVersion>{version}.0</FileVersion>", csproj)
    CSPROJ_PATH.write_text(csproj, encoding="utf-8")
    print(f"版本已更新：base {base} → {version}，code → {code}（metadata.json 与 csproj 已同步）")
    return version


def upload(platform_list: list[str], tag: str | None, title: str | None,
           notes_file: str | None, prerelease: bool) -> None:
    """构建 → 计算 SHA256 → 生成伴生 .sha256 → gh release create。

    gh CLI 不可用时打印完整手动上传清单后退出。
    """
    base = load_metadata()["version"]["base"]
    tag = tag or f"v{base}"
    title = title or f"AyakaLauncher {tag.lstrip('v')}"
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)

    # ---- 1. 构建各平台 + 生成产物与伴生校验文件 ----
    files: list[Path] = []
    for plat in platform_list:
        publish("Release", plat)
        exe = ARTIFACT / "AyakaLauncher.exe"
        if not exe.exists():
            fail(f"未找到 {exe}")
        asset = RELEASE_DIR / f"AyakaLauncher_Release_{plat}.exe"
        shutil.copyfile(exe, asset)
        digest = hashlib.sha256(asset.read_bytes()).hexdigest()
        sha_file = asset.with_name(asset.name + ".sha256")
        sha_file.write_text(digest + "\n", encoding="ascii")
        files.extend([asset, sha_file])
        print(f"产物就绪：{asset.name}（{human_size(asset)}）sha256={digest}")

    # ---- 2. 上传（gh CLI）----
    if shutil.which("gh") is None:
        print()
        print("==== 手动上传清单（未检测到 gh CLI）====")
        print(f"  仓库：  https://github.com/{GITHUB_REPO}/releases/new")
        print(f"  Tag：   {tag}{'（勾选 Set as a pre-release）' if prerelease else ''}")
        print(f"  标题：  {title}")
        if notes_file:
            print(f"  说明：  {notes_file}")
        for f in files:
            print(f"  附件：  {f}")
        return

    cmd = ["gh", "release", "create", tag, "--repo", GITHUB_REPO, "--title", title]
    if prerelease:
        cmd.append("--prerelease")
    if notes_file:
        cmd += ["--notes-file", notes_file]
    cmd += [str(f) for f in files]
    print(f"创建 Release：{tag} …")
    result = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        print(result.stdout, end="")
        if result.stderr:
            print(result.stderr, end="")
        fail(f"gh release create 失败（{result.returncode}）")
    if result.stdout.strip():
        print(result.stdout.strip())


def main() -> None:
    setup_stdio()
    parser = argparse.ArgumentParser(description="AyakaLauncher 发布打包脚本")
    parser.add_argument("--configuration", default="Release", help="构建配置（默认 Release）")
    parser.add_argument("--platform", default="auto", help="目标平台（默认 auto：自动探测本机架构 x86_64→x64 / ARM64→arm64；--upload 模式支持逗号分隔多平台）")
    parser.add_argument("--with-bundle", action="store_true", help="额外打包 exe + runtime 整合包")
    parser.add_argument("--bump", action="store_true", help="同步升级版本号（metadata.json + csproj）")
    parser.add_argument("--version", default=None, help="--bump 指定版本号 X.Y.Z（缺省 minor+1、patch 归零）")
    parser.add_argument("--code", type=int, default=None, help="--bump 指定版本 code（缺省 +1）")
    parser.add_argument("--upload", action="store_true", help="构建并上传 GitHub Release（需 gh CLI，缺失时打印手动清单）")
    parser.add_argument("--tag", default=None, help="--upload 指定 Release tag（缺省 v<metadata base>）")
    parser.add_argument("--release-title", default=None, help="--upload 指定 Release 标题")
    parser.add_argument("--notes-file", default=None, help="--upload 指定说明文件路径")
    parser.add_argument("--prerelease", action="store_true", help="--upload 标记为预发布（对应 beta 更新通道）")
    args = parser.parse_args()

    # ---- 版本升级 / Release 上传模式 ----
    if args.bump:
        bump(args.version, args.code)
        if not args.upload:
            return
    if args.upload:
        platform_list = [p.strip() for p in args.platform.split(",") if p.strip()]
        if any(p.lower() == "auto" for p in platform_list):
            detected, raw_arch = detect_platform()
            print(f"自动探测本机架构：{raw_arch} → 目标平台 {detected}")
            platform_list = [detected if p.lower() == "auto" else p for p in platform_list]
        upload(platform_list, args.tag, args.release_title, args.notes_file, args.prerelease)
        return

    configuration, platform = args.configuration, args.platform
    if platform.lower() == "auto":
        platform, raw_arch = detect_platform()
        print(f"自动探测本机架构：{raw_arch} → 目标平台 {platform}")

    # ---- 0. 前置校验 ----
    # 0.1 项目根必须存在 runtime/ 目录；缺失时提示先获取网盘分发的 runtime.7z。
    if not RUNTIME_DIR.is_dir():
        fail(f"未检测到项目根目录下的 runtime\\ 目录（{RUNTIME_DIR}）。请先从网盘下载 runtime.7z 并解压到项目根目录后，再运行 release.py。")

    # 0.2 runtime/images 四个镜像必须齐备，避免产出残缺的运行时包。
    for image in REQUIRED_IMAGES:
        image_path = RUNTIME_IMAGES / image
        if not image_path.exists():
            fail(f"缺少运行时镜像：{image_path}。请先补齐镜像包（可从网盘 runtime.7z 解压到 runtime\\ 目录）后重试。")

    # ---- 1. Release 发布 ----
    publish(configuration, platform)

    # ---- 2. 复制 exe 到 release（命名与 Release 附件一致）----
    exe = ARTIFACT / "AyakaLauncher.exe"
    if not exe.exists():
        fail(f"未找到 {exe}")
    version = get_file_version(exe)
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)

    published_exe = RELEASE_DIR / f"AyakaLauncher_{configuration}_{platform}.exe"
    exe_target = Path(str(exe) + ".tmp")
    exe_target.write_bytes(exe.read_bytes())
    exe_target.replace(published_exe)

    # ---- 2.5 同步本机运行目录 dist\（本机日常运行布局：exe + runtime 联接 + 数据）----
    # dist 不存在时静默跳过（CI / 其他机器）；exe 被占用（启动器正在运行）时只告警不失败
    dist_dir = ROOT / "dist"
    if dist_dir.is_dir():
        dist_exe = dist_dir / "AyakaLauncher.exe"
        try:
            exe_target.write_bytes(exe.read_bytes())
            exe_target.replace(dist_exe)
            print(f"已同步本机运行目录：{dist_exe}")
        except PermissionError:
            print("警告：dist\\AyakaLauncher.exe 被占用（启动器正在运行？），本次未同步；关闭启动器后重跑或手动复制 artifact\\AyakaLauncher.exe。")

    # ---- 3. 打包 runtime.zip ----
    zip_runtime = RELEASE_DIR / "runtime.zip"
    print("打包 runtime.zip（runtime 约 4.2GB，稍候）…")
    entries = runtime_entries()
    compress_entries(zip_runtime, "打包 runtime.zip", entries)

    # ---- 4. 可选：整合包（exe + runtime）----
    bundle = None
    if args.with_bundle:
        bundle = RELEASE_DIR / f"AyakaLauncher_{version}_{configuration}_{platform}.zip"
        print("打包整合包（exe + runtime，约 4.2GB，稍候）…")
        compress_entries(bundle, "打包整合包", [(exe, "AyakaLauncher.exe")] + entries)

    # ---- 5. 产物清单 ----
    print()
    print("==== 发布产物 ====")
    print(f"  {published_exe}（{human_size(published_exe)}）")
    print(f"  {zip_runtime}（{human_size(zip_runtime)}）")
    if bundle is not None:
        print(f"  {bundle}（{human_size(bundle)}）")
    print()
    print("发布完成。release/runtime.zip 仅供本机整合包与离线分发，不再随 Release 发布；运行时包统一由网盘 runtime.7z 分发。")


if __name__ == "__main__":
    main()
