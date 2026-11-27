#!/bin/bash
# 「中文常用版」精简脚本（由 tools/runtime/texlive-cn/Dockerfile 的 trim 阶段调用）
#
# 输入镜像状态：scheme-full 全量安装（约 5300 包 / 4.8GB texlive）。
# 输出状态：约 1400 包 / 1.2GB texlive，保留集中文常用排版所需宏包。
#
# tlmgr remove 语义（TL2026 实测）：
#   - remove --force <collection>：仅移除集合元包，成员保留；
#   - remove --force <scheme>：向下级联移除几乎所有包（危险，禁用）；
#   - remove <pkg>（不带 --force）：受反向依赖保护，被保留包依赖的包会跳过。
# 因此流程为：先丢弃不需要的集合元包（禁用其成员的集合保护），
# 再从 tlpkg/texlive.tlpdb 计算保留闭包，显式按包名移除其余叶子包。
set -u

TLPTDB=/usr/local/texlive/2026/tlpkg/texlive.tlpdb
YEAR=2026

echo "[trim] sizes before:"
du -sh "/usr/local/texlive"

# ---------------------------------------------------------------------------
# STEP 1 丢弃「不需要集合」的元包（--force 仅移除元包本身，成员不动）
# ---------------------------------------------------------------------------
tlmgr remove --force \
  collection-latexextra collection-publishers collection-binextra \
  collection-context collection-fontsextra collection-formatsextra \
  collection-fontutils collection-games collection-humanities \
  collection-langarabic collection-langczechslovak collection-langcyrillic \
  collection-langeuropean collection-langfrench collection-langgerman \
  collection-langgreek collection-langitalian collection-langjapanese \
  collection-langkorean collection-langother collection-langpolish \
  collection-langportuguese collection-langspanish collection-luatex \
  collection-metapost collection-music collection-plaingeneric \
  collection-pstricks collection-texworks \
  > /tmp/trim-meta.log 2>&1
echo "[trim] unwanted collection metas dropped"

# ---------------------------------------------------------------------------
# STEP 2 保留闭包：种子集合/包 + 它们在 tlpdb 中的依赖闭包
# ---------------------------------------------------------------------------
cat > /tmp/trim-seed.txt <<'EOF'
scheme-basic
collection-basic
collection-latex
collection-latexrecommended
collection-mathscience
collection-pictures
collection-bibtexextra
collection-fontsrecommended
collection-xetex
collection-langchinese
collection-langcjk
collection-langenglish
ctex
xecjk
zhnumber
latexmk
texcount
synctex
systeme
etoolbox
biber
pdfcrop
natbib
gbt7714
EOF
cp /tmp/trim-seed.txt /tmp/trim-keep.txt

for _i in 1 2 3 4 5 6; do
  awk 'NR==FNR{k[$1]=1;next}
       /^name /{pkg=$2; inK=(pkg in k)}
       inK && /^depend /{d=$2; sub(/\.(win32|win64|amd64|arm64|i386|universal|universal-darwin|x86_64-linux|aarch64-linux)$/, "", d); if (d != "") print d}' \
      /tmp/trim-keep.txt "$TLPTDB" | sort -u > /tmp/trim-keep.new
  cat /tmp/trim-seed.txt >> /tmp/trim-keep.new
  sort -u /tmp/trim-keep.new > /tmp/trim-keep.next
  if cmp -s /tmp/trim-keep.txt /tmp/trim-keep.next; then break; fi
  mv /tmp/trim-keep.next /tmp/trim-keep.txt
done
echo "[trim] keep closure: $(wc -l < /tmp/trim-keep.txt) packages"

# ---------------------------------------------------------------------------
# STEP 3 计算移除清单并执行（不带 --force，保留闭包外的反向依赖包自动跳过）
# ---------------------------------------------------------------------------
tlmgr list --only-installed 2>/dev/null | sed 's/^i //; s/:.*//' | sort -u > /tmp/trim-installed.txt
echo "[trim] installed: $(wc -l < /tmp/trim-installed.txt)"

# scheme-*/collection-* 元包不进入显式移除清单：
#   scheme-full 若被移除会向下级联清空全部保留包（实测 5300 -> 183）。
awk 'NR==FNR{k[$1]=1;next}
     {b=$1; sub(/\.(x86_64-linux|i386-linux|aarch64-linux|win32|win64)$/,"",b);
      if (!(b in k) && $1 !~ /^(scheme-|collection-)/) print $1}' \
    /tmp/trim-keep.txt /tmp/trim-installed.txt > /tmp/trim-remove.txt
echo "[trim] to remove: $(wc -l < /tmp/trim-remove.txt) leaf packages"

tlmgr remove $(cat /tmp/trim-remove.txt) > /tmp/trim-remove.log 2>&1 || true
echo "[trim] removed: $(grep -c 'remove:' /tmp/trim-remove.log || true) packages"
echo "[trim] skipped (reverse deps): $(grep -c 'not removing' /tmp/trim-remove.log || true) packages"

# ---------------------------------------------------------------------------
# STEP 4 刷新文件名数据库
# ---------------------------------------------------------------------------
mktexlsr > /dev/null 2>&1
echo "[trim] sizes after:"
du -sh "/usr/local/texlive"
echo "[trim] installed now: $(tlmgr list --only-installed 2>/dev/null | wc -l)"

# ---------------------------------------------------------------------------
# STEP 5 镜像级清理：apt 缓存/日志/临时文件（spec：移除冗余文档与源码）
# ---------------------------------------------------------------------------
rm -rf /var/lib/apt/lists/* /var/cache/apt/* /var/log/* /tmp/* /var/tmp/* 2>/dev/null || true
find "/usr/local/texlive/$YEAR" -name '*.log' -type f -delete 2>/dev/null || true
echo "[trim] apt/log cleanup done"
