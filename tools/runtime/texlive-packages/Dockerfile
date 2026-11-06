#
# TeX Live 常用宏包增量层（中文排版 + 科研论文）
# -------------------------------------------------
# 上游 ayakaleaf-pro 的 server-ce/Dockerfile-base 已把同样的包列表固化进
# 其 tlmgr install 层。本文件用于在不重建 baseimage / apt / Node / yarn 的
# 前提下，基于已发布的 Overleaf 成品镜像产出等价的运行时镜像，
# 再通过 `wslc save` 导出为 runtime/images/sharelatex-server-pro.tar.gz。
#
# 构建（验证用临时 tag）：
#   wslc build -f tools/runtime/texlive-packages/Dockerfile \
#     -t sharelatex/sharelatex:server-pro-tex tools/runtime/texlive-packages \
#     --build-arg TEXLIVE_MIRROR=https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet
#
# 导出（覆盖启动器默认使用的 tag）：
#   wslc build -f tools/runtime/texlive-packages/Dockerfile \
#     -t sharelatex/sharelatex:server-pro tools/runtime/texlive-packages \
#     --build-arg TEXLIVE_MIRROR=https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet
#   wslc save -o sharelatex-server-pro.tar sharelatex/sharelatex:server-pro
#   gzip -6 -c sharelatex-server-pro.tar > sharelatex-server-pro.tar.gz
#
# 包集合说明：
#   collection-latexrecommended  常用 LaTeX 推荐包（babel、microtype 等）
#   collection-latexextra        booktabs/enumitem/caption/float/wrapfig/
#                                listings/tcolorbox/algorithm2e/mathtools 等
#   collection-pictures          pgfplots/tikz 扩展/circuitikz 等
#   （collection-science 在 TeX Live 2026 仓库中已不存在，安装会报
#     "package collection-science not present"；siunitx/mhchem 等由
#     collection-latexextra / collection-mathscience 提供）
#   collection-mathscience       amsmath 扩展、unicode-math、theorem 工具等
#   collection-bibtexextra       biblatex 与各类引用样式（含 biber 依赖）
#   collection-publishers        期刊模板：elsarticle/revtex/IEEEtran/
#                                acmart/nature/science/aa/mnras 等
#   collection-fontsrecommended  推荐字体（mathpazo、txfonts、newtx 等）
#   collection-binextra          bibtex/biber 工具链
#   systeme                      方程组排版（不属于上述集合，单独补装）
ARG OVERLEAF_BASE_TAG=sharelatex/sharelatex:server-pro
FROM ${OVERLEAF_BASE_TAG}

# 与 server-ce/Dockerfile-base 保持一致的 CTAN 源默认值
ARG TEXLIVE_MIRROR=https://mirror.ox.ac.uk/sites/ctan.org/systems/texlive/tlnet

# install-tl 自带的 tlmgr 可能旧于仓库 tlpdb，直接 install 会中止，
# 因此先刷新 tlmgr 自身，再安装中文宏包、字体与常用包集合。
#
# tlmgr install 只要有一个包下载失败（CTAN 镜像偶发 5xx/超时）就整体返回
# 非 0，即使其余 3000+ 包都已装好。这里最多重试 3 次：已装的包会被跳过，
# 重试只补失败的包，代价很小。
RUN tlmgr update --self --repository ${TEXLIVE_MIRROR} \
 && ( \
      for attempt in 1 2 3; do \
        tlmgr install --repository ${TEXLIVE_MIRROR} \
          ctex \
          xecjk \
          fandol \
          collection-latexrecommended \
          collection-latexextra \
          collection-pictures \
          collection-mathscience \
          collection-bibtexextra \
          collection-publishers \
          collection-fontsrecommended \
          collection-binextra \
          systeme \
        && exit 0; \
        echo "tlmgr install attempt $$attempt failed, retrying..." >&2; \
        sleep 10; \
      done; \
      exit 1; \
    ) \
 && tlmgr path add \
 && mktexlsr