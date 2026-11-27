#
# 「中文常用版」精简 TeX Live 变体（US3 / T011）
# -------------------------------------------------
# 目标：新用户可选 ≤2GB 精简镜像，典型中文文档（正文 + 公式 + 图表 + 参考文献）
# 开箱可编译；学科专用/期刊模板宏包不内置，缺包时经「服务台 → 宏包管理」在线补装。
#
# 保留集（详见 trim.sh 注释）：
#   scheme-basic + collection-basic/latex/latexrecommended/mathscience/
#   pictures/bibtexextra/fontsrecommended/xetex/langchinese/langcjk/langenglish
#   + 显式包（ctex xecjk zhnumber latexmk texcount synctex biber gbt7714 等）
# 移除集：
#   context/fontsextra/latexextra/publishers/各小语种/pstricks/luatex/
#   humanities/games/music/metapost 等 29 个集合的全部成员（约 3900 包）
#
# 双阶段构造（关键）：
#   阶段一 trim：tlmgr remove 裁剪 —— 文件系统变小，但 Docker 层仍叠加，
#               直接 save 体积不变；
#   阶段二 squash：FROM scratch + COPY --from=trim / / —— 把裁剪后的根文件
#               系统复制进单层镜像（buildkit 按合并视图复制，删除生效），
#               镜像配置（ENV/ENTRYPOINT/WORKDIR/EXPOSE）逐项重新声明，
#               与上游 server-pro 镜像 wslc inspect 输出保持一致。
#   产物单层、体积即裁剪后的真实大小，`wslc save` 导出即为精简 tar.gz。
#
# 构建（前置：本地已有 pandoc 阶段镜像 sharelatex/sharelatex:server-pro-pandoc，
#       通常由 tools/runtime/build-images.py 第 ① 步产出）：
#   wslc build -f tools/runtime/texlive-cn/Dockerfile \
#     -t sharelatex/sharelatex:server-pro-cn \
#     --build-arg OVERLEAF_BASE_TAG=sharelatex/sharelatex:server-pro-pandoc \
#     tools/runtime/texlive-cn
#   wslc save -o sharelatex-server-pro-cn.tar sharelatex/sharelatex:server-pro-cn
#   gzip -9 -c sharelatex-server-pro-cn.tar > sharelatex-server-pro-cn.tar.gz
#
# 验收门（构建即验证，失败即终止构建）：
#   1) 保留集核心文件 kpsewhich 全通过；
#   2) 典型中文文档（ctexart + amsmath + booktabs + pgfplots + biblatex）
#      latexmk -xelatex 编译出 PDF。
ARG OVERLEAF_BASE_TAG=sharelatex/sharelatex:server-pro-pandoc

# ---------- 阶段一：裁剪 ----------
FROM ${OVERLEAF_BASE_TAG} AS trim

COPY trim.sh /tmp/trim.sh
RUN bash /tmp/trim.sh && rm -f /tmp/trim.sh

# 保留集核心文件校验：缺一个即构建失败，避免「构建成功但精简过头」。
RUN kpsewhich ctex.sty       > /dev/null \
 && kpsewhich xeCJK.sty      > /dev/null \
 && kpsewhich zhnumber.sty   > /dev/null \
 && kpsewhich booktabs.sty   > /dev/null \
 && kpsewhich amsmath.sty    > /dev/null \
 && kpsewhich pgfplots.sty   > /dev/null \
 && kpsewhich biblatex.sty   > /dev/null \
 && kpsewhich gbt7714.sty    > /dev/null \
 && kpsewhich lmodern.sty    > /dev/null \
 && command -v latexmk       > /dev/null \
 && command -v biber         > /dev/null \
 && command -v texcount      > /dev/null \
 && command -v pandoc        > /dev/null \
 && echo "cn keep-set check OK"

# 典型中文文档编译验收（正文 + 公式 + 图表 + 参考文献，xelatex 全链路）。
RUN mkdir -p /tmp/cn-gate && cd /tmp/cn-gate \
 && printf '%s\n' \
    '\documentclass[UTF8]{ctexart}' \
    '\usepackage{amsmath,booktabs,pgfplots,biblatex}' \
    '\pgfplotsset{compat=1.18}' \
    '\begin{document}' \
    '中文排版验证：Ayakaleaf 精简镜像。' \
    '\begin{equation}E = mc^{2}\end{equation}' \
    '\begin{tabular}{lr}\toprule 科目 & 分数 \\ \midrule 语文 & 95 \\ 数学 & 98 \\ \bottomrule\end{tabular}' \
    '\begin{tikzpicture}\begin{axis}\addplot {x^2};\end{axis}\end{tikzpicture}' \
    '\end{document}' > t.tex \
 && latexmk -xelatex -interaction=nonstopmode t.tex > /tmp/cn-gate/latexmk.log 2>&1 \
 && test -s t.pdf \
 && cd / && rm -rf /tmp/cn-gate \
 && echo "cn compile gate OK"

# ---------- 阶段二：单层压缩（squash） ----------
# 配置来源：wslc inspect sharelatex/sharelatex:server-pro 的 Config 输出，
# 逐项保持一致；后续上游镜像配置变化时需同步更新这里。
FROM scratch

COPY --from=trim / /

ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    DEBIAN_FRONTEND=teletype \
    LANG=en_US.UTF-8 \
    LANGUAGE=en_US:en \
    LC_ALL=en_US.UTF-8 \
    TEXMFVAR=/var/lib/overleaf/tmp/texmf-var \
    REBUILT_AFTER=2026-08-27 \
    COREPACK_HOME=/opt/corepack \
    COREPACK_ENABLE_NETWORK=0 \
    SITE_MAINTENANCE_FILE=/etc/overleaf/site_status \
    OVERLEAF_CONFIG=/etc/overleaf/settings.js \
    WEB_API_USER=overleaf \
    ADMIN_PRIVILEGE_AVAILABLE=true \
    OPTIMISE_PDF=true \
    REQ_VALIDATION_MODE=enforce-log \
    KILL_PROCESS_TIMEOUT=55 \
    KILL_ALL_PROCESSES_TIMEOUT=55 \
    GRACEFUL_SHUTDOWN_DELAY_SECONDS=1 \
    NODE_ENV=production \
    LOG_LEVEL=info
# 含空格的值必须单条声明且只给值加引号（引号包住整个 key=value 会被当作非法 token）
ENV NODE_OPTIONS="--require /overleaf/.pnp.cjs --import /overleaf/.pnp.register.mjs"
ENV OVERLEAF_APP_NAME="Overleaf Community Edition"

WORKDIR /overleaf

EXPOSE 80

ENTRYPOINT ["/sbin/my_init"]

LABEL com.overleaf.ce.revision="21856f9a99cebb841ded77a56a79a94359628b67" \
      org.opencontainers.image.version="24.04" \
      org.opencontainers.image.title="Ayakaleaf Overleaf server-pro (cn-lite)"
