#
# TeX Live 完整发行版（scheme-full）
# -------------------------------------------------
# 在已发布的 Overleaf 成品镜像之上安装 scheme-full（约 4900 个包，含全部
# 语言、字体、期刊模板与文档），产出"开箱即全"的运行时镜像，再通过
# `wslc save` 导出为 runtime/images/sharelatex-server-pro.tar.gz。
#
# 与 texlive-packages/Dockerfile（精选集合）的区别：scheme-full 是超集，
# 覆盖 ctex/xecjk、全部 collection-*、systeme 等所有包，无需逐个列举。
#
# 构建（验证用临时 tag）：
#   wslc build -f tools/runtime/texlive-full/Dockerfile \
#     -t sharelatex/sharelatex:server-pro-tex-full tools/runtime/texlive-full \
#     --build-arg TEXLIVE_MIRROR=https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet
#
# 验证通过后转正为启动器默认 tag 并导出（覆盖 sharelatex-server-pro.tar.gz）：
#   wslc tag sharelatex/sharelatex:server-pro-tex-full sharelatex/sharelatex:server-pro
#   wslc save -o sharelatex-server-pro.tar sharelatex/sharelatex:server-pro
#   gzip -6 -c sharelatex-server-pro.tar > sharelatex-server-pro.tar.gz
#
# 体积与耗时提示：scheme-full 使 texlive 增至约 7-8GB（含文档），
# 从 CTAN 镜像下载约 4-5GB，安装约 4900 个包，构建通常需要 1 小时上下。
ARG OVERLEAF_BASE_TAG=sharelatex/sharelatex:server-pro
FROM ${OVERLEAF_BASE_TAG}

# 与 server-ce/Dockerfile-base 保持一致的 CTAN 源默认值
ARG TEXLIVE_MIRROR=https://mirror.ox.ac.uk/sites/ctan.org/systems/texlive/tlnet

# install-tl 自带的 tlmgr 可能旧于仓库 tlpdb，直接 install 会中止，
# 因此先刷新 tlmgr 自身，再安装 scheme-full。
#
# scheme-full 一次性拉取 4900+ 个包，CTAN 镜像偶发 5xx/超时就会整体失败；
# 已装的包会被跳过，重试只补失败的包。最多重试 5 次，每次失败后退避 15 秒。
RUN tlmgr update --self --repository ${TEXLIVE_MIRROR} \
 && ( \
      for attempt in 1 2 3 4 5; do \
        tlmgr install --repository ${TEXLIVE_MIRROR} scheme-full \
        && exit 0; \
        echo "tlmgr install attempt $$attempt failed, retrying..." >&2; \
        sleep 15; \
      done; \
      exit 1; \
    ) \
 && tlmgr path add \
 && mktexlsr
