#
# TeX Live 常用宏包增量层（中文排版 + 科研论文 + 学科专用）
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
#   （fandol 同理：2026-11-23 起 CTAN 的 fandol 0.4 只保留 Braille 字体
#     （拆成 fandol-braille），中文 OTF 被标为 obsolete 并从 TL 仓库移除，
#     现在 `tlmgr install fandol` 报 "not present in repository" 导致整层失败。
#     已发布镜像里 Fandol 字体仍在（scheme-full 早于该变更），故本层不再安装；
#     从零重建且需要 fontset=fandol 时，得自备字体或改用 lxgw-fonts + fontset=none）
#   collection-mathscience       amsmath 扩展、unicode-math、theorem 工具等
#   collection-bibtexextra       biblatex 与各类引用样式（含 biber 依赖）
#   collection-publishers        期刊模板：elsarticle/revtex/IEEEtran/
#                                acmart/nature/science/aa/mnras 等
#   collection-fontsrecommended  推荐字体（mathpazo、txfonts、newtx 等）
#   collection-binextra          bibtex/biber 工具链
#   systeme                      方程组排版（不属于上述集合，单独补装）
#
# 学科补充（物理 / 化学 / 生物 / 地理 / 编程语言）：
#   集合成员随 TeX Live 版本变动（TL2026 就删掉了 collection-science），
#   只靠 collection-* 会在升级后静默缺包。因此集合之外再按学科显式列一遍，
#   已由集合带入的条目 tlmgr 直接跳过，不重复下载，只多几十毫秒解析时间。
#   物理  physics braket tensor hepunits axodraw2 simpler-wick youngtab isotope
#         quantikz qcircuit tikz-feynman tikz-feynhand
#         费曼图的两条老路线另需二进制：metapost（提供 mpost/mf）+ feynmf
#   化学  mhchem chemformula chemfig chemmacros chemgreek chemnum chemcompounds
#         chemarrow chemplants chemsec chemstyle ghsystem modiagram bpchem
#         xymtex bohr achemso biblatex-chem
#   生物  texshade（序列比对着色）dnaseq pgfmolbio（分子生物学图）
#         biocon（物种名双名法）seqsplit forest（进化树）biblatex-science
#         biblatex-nature
#   地理  pst-geo + pstricks（地图投影；pst-geo 未声明依赖，需显式带 pstricks）
#         worldflags（TikZ 国旗）datatool csvsimple（数据驱动制图）
#         tikz-3dplot tikzscale pgf-pie marvosym wasysym fontawesome5（图标）
#   编程  minted fvextra lstaddons listingsutf8 cprotect
#         algorithms（algorithm.sty）algorithmicx（algpseudocode）
#         matlab-prettifier pseudocode syntax
#
# 注意 minted：镜像里只有 minted.sty，实际编译还需要 -shell-escape 与
# python 侧 latexminted（TeX Live 2025 起 minted v3 的硬依赖）。默认不装，
# 避免构建期多一条 pip 外网依赖；确需代码高亮时取消文末注释的 apt/pip 层，
# 并在编译参数里允许 shell-escape，否则请改用 listings。
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
          systeme \
          collection-latexrecommended \
          collection-latexextra \
          collection-pictures \
          collection-mathscience \
          collection-bibtexextra \
          collection-publishers \
          collection-fontsrecommended \
          collection-binextra \
          metapost \
          feynmf \
          pstricks \
          pst-geo \
          fontawesome5 \
          physics braket tensor hepunits axodraw2 simpler-wick youngtab isotope \
          quantikz qcircuit tikz-feynman tikz-feynhand \
          mhchem chemformula chemfig chemmacros chemgreek chemnum chemcompounds \
          chemarrow chemplants chemsec chemstyle ghsystem modiagram bpchem \
          xymtex bohr achemso biblatex-chem \
          texshade dnaseq pgfmolbio biocon seqsplit forest \
          biblatex-science biblatex-nature \
          worldflags datatool csvsimple tikz-3dplot tikzscale pgf-pie \
          marvosym wasysym \
          minted fvextra lstaddons listingsutf8 cprotect \
          algorithms algorithmicx matlab-prettifier pseudocode syntax \
        && exit 0; \
        echo "tlmgr install attempt failed, retrying..." >&2; \
        sleep 10; \
      done; \
      exit 1; \
    ) \
 && tlmgr path add \
 && mktexlsr
# 装完立刻做一次文件级校验，避免集合变动后构建"成功但缺包"。
# 清单取各学科的代表性 .sty/.cls，全部实测存在于 TeX Live 2026。
# 逐个 kpsewhich 而不用 for 循环，避开多层 shell 对 $ 变量的转义坑。
RUN kpsewhich physics.sty         > /dev/null \
 && kpsewhich braket.sty          > /dev/null \
 && kpsewhich feynmf.sty          > /dev/null \
 && kpsewhich tikz-feynman.sty    > /dev/null \
 && kpsewhich chemfig.sty         > /dev/null \
 && kpsewhich xymtex.sty          > /dev/null \
 && kpsewhich chemplants.sty      > /dev/null \
 && kpsewhich achemso.cls         > /dev/null \
 && kpsewhich texshade.sty        > /dev/null \
 && kpsewhich pgfmolbio.sty       > /dev/null \
 && kpsewhich biocon.sty          > /dev/null \
 && kpsewhich pst-geo.sty         > /dev/null \
 && kpsewhich worldflags.sty      > /dev/null \
 && kpsewhich datatool.sty        > /dev/null \
 && kpsewhich minted.sty          > /dev/null \
 && kpsewhich algorithm.sty       > /dev/null \
 && kpsewhich algpseudocode.sty   > /dev/null \
 && kpsewhich syntax.sty          > /dev/null \
 && echo "subject package check OK"

# 可选：为 minted 补 python 依赖（默认关闭，见文件头说明）
# RUN apt-get update \
#  && apt-get install -y --no-install-recommends python3-pip \
#  && pip3 install --no-cache-dir latexminted \
#  && rm -rf /var/lib/apt/lists/*
