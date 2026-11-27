#
# Pandoc 转换层（Word / Markdown 文档导入）
# -------------------------------------------------
# 背景：Overleaf 的 .docx / .md 导入由 CLSI 的 ConversionManager 调用 pandoc 完成。
# CLSI 只有在 DOCKER_RUNNER=true / SANDBOXED_COMPILES=true 时才启用 DockerRunner
# （config/settings.defaults.cjs），本项目容器没有 docker socket（WSLC 下不支持同级容器），
# 转换命令走 LocalCommandRunner —— 在 CLSI 容器内直接执行 `pandoc`。
# 镜像里没有这个可执行文件时，导入一律失败（日志："pandoc conversion failed"）。
#
# 版本要求：ConversionManager 实际调用
#   pandoc --extract-media=. --from docx+citations --citeproc ...
# `--citeproc` 需 pandoc >= 2.11，`docx+citations` 需 3.x；
# Ubuntu 22.04 仓库只有 2.9（unknown option 直接失败），故取官方静态包。
# 官方 pandoc 镜像（quay.io/sharelatex/pandoc:3.9）匿名拉取被拒，也不采用。
#
# 前置：构建上下文需要先备好静态包（容器网络到不了 GitHub，宿主机可以）：
#   python tools/runtime/pandoc/fetch-pandoc.py
#   # 国内网络慢可走代理前缀：
#   python tools/runtime/pandoc/fetch-pandoc.py \
#       --mirror https://ghfast.top/https://github.com/jgm/pandoc/releases/download
#
# 构建：
#   wslc build -f tools/runtime/pandoc/Dockerfile \
#     -t sharelatex/sharelatex:server-pro-pandoc tools/runtime/pandoc
#   （串联顺序：Dockerfile-base → pandoc 层 → texlive 层）
ARG OVERLEAF_BASE_TAG=sharelatex/sharelatex:server-pro
FROM ${OVERLEAF_BASE_TAG}

ARG PANDOC_VERSION=3.9

# 静态包由 fetch-pandoc.py 放进 vendor/（已 gitignore），通配符兼容 amd64/arm64
COPY vendor/pandoc-${PANDOC_VERSION}-linux-*.tar.gz /tmp/pandoc.tgz

RUN set -eux; \
    tar -xzf /tmp/pandoc.tgz -C /tmp; \
    install -m 0755 "/tmp/pandoc-${PANDOC_VERSION}/bin/pandoc" /usr/local/bin/pandoc; \
    rm -rf /tmp/pandoc.tgz "/tmp/pandoc-${PANDOC_VERSION}"; \
    pandoc --version | head -2; \
    pandoc --list-extensions=docx | grep -q citations
