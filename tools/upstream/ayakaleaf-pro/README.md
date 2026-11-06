# 上游构建定义快照

来源：[ayaka-notes/ayakaleaf-pro](https://github.com/ayaka-notes/ayakaleaf-pro)（分支 `server-pro`，基线 commit `2b5d8208b0`）。

`server-ce/Dockerfile-base` 为完整重建 `sharelatex/sharelatex:server-pro` 镜像的构建定义本地快照，相对上游原版包含以下修改：

- `tlmgr update --self` 前置：install-tl 自带的 tlmgr 旧于仓库 tlpdb，直接 `tlmgr install` 会中止
- `tlmgr install` 包列表固化：`ctex`、`xecjk`、`fandol` + `collection-latexrecommended` / `latexextra` / `pictures` / `mathscience` / `bibtexextra` / `publishers` / `fontsrecommended` / `binextra` + `systeme`
- 安装循环最多重试 3 次（已装包自动跳过）：批量安装 3000+ 包时任一包下载失败会使 tlmgr 整体返回 1
- `collection-science` 在 TeX Live 2026 仓库中已不存在，未列入（siunitx/mhchem 由 latexextra / mathscience 提供）

不重建完整镜像、只给已发布镜像补包时，用 `tools/runtime/texlive-packages/Dockerfile`（增量层，与本快照的包列表保持一致）。