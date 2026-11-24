/*
 * Ayakaleaf 工作台编辑器联动
 * ---------------------------------------------------------------------------
 * 功能：在左侧代码栏里选中一段 LaTeX 公式代码时，右侧 PDF 渲染栏自动高亮
 *      对应位置并滚动到那里。
 *
 * 实现思路（不新增接口，全部复用官方链路）：
 *   1. 通过 window.overleaf.unstable.store 拿到当前 CodeMirror 视图与文档全文；
 *   2. 用轻量扫描器收集文中所有数学环境区间（$..$ / $$..$$ / \(..\) / \[..\] /
 *      equation、align、gather 等 \begin{} 环境），只有选区落在公式里才继续；
 *   3. 把同步目标定死在公式起点所在行：派发官方的 'cursor:editor:syncToPdf' 事件，
 *      同时把目标行写进公式标记，由第二段包装的 fetch 直接改写 /sync/code 的 line
 *      参数（只靠伪造光标事件会被编辑器自身的 cursor 更新抢走，实测会取到选区末端），
 *      之后的 /sync/code、画高亮框、滚动定位仍由 Overleaf 自带 use-synctex 完成。
 *
 * 第二段（高亮修正，由 /sync/code 实测数据驱动）：
 *   A. 拦截 /sync/code 响应做几何消歧，修复多栏误亮：
 *      - 同一矩形会被 synctex 重复返回 5~36 次 → 去重；
 *      - display/align 会产生 height=0 的整栏宽退化矩形（高亮不可见）→ 丢弃，
 *        整页只剩退化矩形时回退保留（保住 scrollIntoView 滚动行为）；
 *      - 多行环境（align 后续行等）同时返回「整栏宽行盒」和「内容级小盒」，
 *        行盒顶部范围内若落有更窄的小盒，说明它是行级噪音 → 丢弃；
 *      - 行内公式只返回行盒（synctex 无更细粒度）→ 原样保留，行为与官方一致；
 *      - tikz/tabular 等大盒内容会连带返回「从该行到页底」的容器盒（高数百 bp、
 *        整片空白）→ 单条公式不可能那么高，同页还有实心矩形时丢弃。
 *   B. MutationObserver 监听高亮元素出现，用 canvas 墨迹扫描把高亮贴到内容上：
 *      - 先收窄：synctex 盒子常带留白（tikz 坐标轴外边距、整行行盒的列宽），
 *        把矩形收缩到自身范围内的墨迹边界，高亮才贴住内容而不是空白；
 *      - 再扩框（不足以覆盖公式墨迹时）：高分式/矩阵纵向溢出、align 首行
 *        height=0 不可见，从矩形四周容忍少量空白向墨迹方向扩张；
 *      - 零高度矩形从原位置上下搜索第一行墨迹作种子（救回 align 首行）；
 *      - 垂直空隙容忍度小于行距（不吞并相邻行），水平容忍度小于栏间距
 *        （不跨栏）；只在几何发生变化时改写样式，画布未渲染时自动重试。
 *   D. 空白高亮剔除：几何过滤漏掉的错位矩形在画布上一点墨迹都没有，
 *      同页已有含墨迹的高亮时直接删除（整页唯一高亮即使是空白也保留）。
 *
 * 注入方式：WebView2 的 AddScriptToExecuteOnDocumentCreatedAsync，每次文档创建都执行，
 *          先于应用 bundle 运行，因此可以安全包装 window.fetch。
 */

(() => {
  'use strict'

  if (window.__ayakaTexSyncSelection) return
  window.__ayakaTexSyncSelection = true

  // 鼠标松开后等 CodeMirror 把 cursor:editor:update 派发出来
  const DEBOUNCE_MS = 300
  // 派发伪造光标位置后，等 React 把位置写进 ref 再触发同步
  const AFTER_CURSOR_UPDATE_MS = 160
  // 同一选区短时间内不重复触发
  const SAME_SELECTION_QUIET_MS = 1200
  // 超长选区（全选、大段复制）不处理
  const MAX_SELECTION_CHARS = 4000

  // 数学环境（\begin{...} 形式），这些环境内部天然处于数学模式
  const MATH_ENVS = new Set([
    'math', 'displaymath',
    'equation', 'equation*',
    'eqnarray', 'eqnarray*',
    'align', 'align*',
    'alignat', 'alignat*',
    'flalign', 'flalign*',
    'gather', 'gather*',
    'multline', 'multline*',
    'split', 'cases', 'cases*',
    'array', 'darray', 'subequations',
    'matrix', 'pmatrix', 'bmatrix', 'Bmatrix', 'vmatrix', 'Vmatrix', 'smallmatrix'
  ])

  // 需要整体跳过的环境，内部可能出现 $ 等字符但并非数学
  const SKIP_ENVS = new Set([
    'verbatim', 'verbatim*', 'Verbatim', 'lstlisting', 'minted',
    'filecontents', 'filecontents*', 'comment', 'alltt'
  ])

  let timer = null
  let lastKey = ''
  let lastKeyAt = 0

  /* ------------------------------------------------------------------ *
   * 数学区间扫描
   * ------------------------------------------------------------------ */

  function collectMathRanges(text) {
    const ranges = []
    const stack = []
    const n = text.length
    let i = 0

    const popTo = (matchFn, end) => {
      for (let k = stack.length - 1; k >= 0; k--) {
        if (matchFn(stack[k])) {
          const entry = stack[k]
          stack.length = k
          // 非数学环境（document／tikzpicture／figure 等）只用于配对，不入区间表：
          // 之前把它们也当成区间，导致 (a) 合并多公式时行号表被垃圾行占满上限、
          // 真公式被挤掉；(b) 反向选择时起点落在外层环境里，被当成命中 → 同步到选区末端。
          if (entry.kind !== 'other') {
            ranges.push({ from: entry.start, to: end, kind: entry.kind, name: entry.name })
          }
          return
        }
      }
    }

    while (i < n) {
      const code = text.charCodeAt(i)

      // 反斜杠：命令、转义、定界符
      if (code === 92) {
        if (text.startsWith('\\begin{', i)) {
          const braceEnd = text.indexOf('}', i + 7)
          if (braceEnd > 0) {
            const name = text.slice(i + 7, braceEnd).trim()
            if (SKIP_ENVS.has(name)) {
              const endToken = '\\end{' + name + '}'
              const endIdx = text.indexOf(endToken, braceEnd)
              i = endIdx < 0 ? n : endIdx + endToken.length
              continue
            }
            stack.push({
              kind: MATH_ENVS.has(name) ? 'env' : 'other',
              name,
              start: i
            })
            i = braceEnd + 1
            continue
          }
        }

        if (text.startsWith('\\end{', i)) {
          const braceEnd = text.indexOf('}', i + 5)
          if (braceEnd > 0) {
            const name = text.slice(i + 5, braceEnd).trim()
            popTo(entry => (entry.kind === 'env' || entry.kind === 'other') && entry.name === name, braceEnd + 1)
            i = braceEnd + 1
            continue
          }
        }

        if (text.startsWith('\\(', i)) {
          stack.push({ kind: 'paren', start: i })
          i += 2
          continue
        }
        if (text.startsWith('\\)', i)) {
          popTo(entry => entry.kind === 'paren', i + 2)
          i += 2
          continue
        }
        if (text.startsWith('\\[', i)) {
          stack.push({ kind: 'bracket', start: i })
          i += 2
          continue
        }
        if (text.startsWith('\\]', i)) {
          popTo(entry => entry.kind === 'bracket', i + 2)
          i += 2
          continue
        }

        // 普通命令或转义字符：跳过下一个字符即可（\$ \% 等不会被误判为定界符）
        i += 2
        continue
      }

      // 注释：整行忽略
      if (code === 37) {
        const nl = text.indexOf('\n', i)
        i = nl < 0 ? n : nl + 1
        continue
      }

      // $ / $$ 行内与行间公式
      if (code === 36) {
        const isDouble = text.charCodeAt(i + 1) === 36
        const kind = isDouble ? 'ddollar' : 'sdollar'
        const len = isDouble ? 2 : 1
        const top = stack[stack.length - 1]

        if (top && top.kind === kind) {
          stack.pop()
          ranges.push({ from: top.start, to: i + len, kind })
        } else if (!(top && (top.kind === 'sdollar' || top.kind === 'ddollar'))) {
          // 行内公式里再遇到不匹配的 $ 一律忽略，避免把后文整体误配
          stack.push({ kind, start: i })
        }
        i += len
        continue
      }

      i++
    }

    ranges.sort((a, b) => a.from - b.from)
    return ranges
  }

  /* ------------------------------------------------------------------ *
   * 编辑器 / 选区读取
   * ------------------------------------------------------------------ */

  function getStore() {
    try {
      return window.overleaf?.unstable?.store ?? null
    } catch {
      return null
    }
  }

  function getEditorView() {
    const store = getStore()
    if (!store || typeof store.get !== 'function') return null
    try {
      const view = store.get('editor.view')
      return view?.state?.doc ? view : null
    } catch {
      return null
    }
  }

  function isProjectPage() {
    return /^\/project\/[0-9a-zA-Z]{8,}/.test(location.pathname)
  }

  function hasPdfPane() {
    // 必须已经渲染出页面（说明至少编译成功过一次），否则触发同步只会拿到报错提示
    return !!document.querySelector('.pdfViewer .page')
  }

  // 选区是否真的落在代码编辑器里（排除聊天框、搜索框等其他选区）
  function selectionInsideEditor(view) {
    const selection = window.getSelection()
    if (!selection || selection.isCollapsed) return false
    const anchor = selection.anchorNode
    if (!anchor) return false
    return view.dom.contains(anchor)
  }

  // 无 editor.view 时（页面结构变动）的兜底判断：只看选中文本本身
  const FALLBACK_MATH_RE =
    /(^|\s)(\$|\\\(|\\\[|\\begin\{(math|displaymath|equation|align|alignat|flalign|gather|multline|eqnarray|split|cases|array|matrix|pmatrix|bmatrix|vmatrix)\*?\})/

  function looksLikeMathText(text) {
    return FALLBACK_MATH_RE.test(text)
  }

  /* ------------------------------------------------------------------ *
   * 触发同步
   * ------------------------------------------------------------------ */

  function dispatchSyncToPdf() {
    window.dispatchEvent(new CustomEvent('cursor:editor:syncToPdf'))
  }

  // 诊断输出（第一段自己实现一份：第二段的 report 尚未定义，运行期才可用）
  function reportDiag(text) {
    try {
      const sink = window.__ayakaReport
      if (typeof sink === 'function') {
        sink(text)
        return
      }
      console.info(text)
    } catch {
      // ignore
    }
  }

  // 记录选区与命中集合，便于对照「扫到了哪几处公式」
  function reportSelection(view, from, to, head, lines, primaryLine) {
    try {
      const fromLine = view.state.doc.lineAt(from).number
      const toLine = view.state.doc.lineAt(to).number
      const headLine = view.state.doc.lineAt(head).number
      reportDiag(
        `[ayaka-sync] 选区 ${fromLine}-${toLine}（head ${headLine}）命中 ${lines.length} 处` +
        ` → [${lines.join(',')}] 主行 ${primaryLine}`
      )
    } catch {
      // ignore
    }
  }

  // 一次同步最多合并多少个公式（选区很大时防止请求风暴）
  const MAX_SYNC_LINES = 8

  // 标记「这次同步由公式选区触发」，Part C（行内公式收窄）读取后清除；
  // 工具栏/光标移动触发的同步不打标记，保持官方行为。
  // lines：选区命中的全部公式起始行（一次同步多个公式时由 Part A2 合并响应）。
  function markFormulaSync(line, lines) {
    try {
      window.__ayakaFormulaSyncPending = {
        line: line ?? null,
        lines: Array.isArray(lines) && lines.length > 0 ? lines.slice(0, MAX_SYNC_LINES) : null,
        at: Date.now()
      }
    } catch {
      // 标记失败最多是不收窄，不能影响同步本身
    }
  }

  // 把光标位置挪到公式起点：官方 syncToPdf 读的是 useSynctex 里缓存的坐标。
  // 只派发 cursor:editor:update 并不保险——编辑器自身的 cursor 更新会和它抢，
  // 实测最终用的是「选区末端」（选中前两张图只亮第二张）。所以在派发事件的同时
  // 先把目标行记进公式标记，由 inject 的 fetch 包装直接改写请求里的 line 参数
  // （见 forcedSyncUrl），行号不再依赖事件竞态。
  function dispatchSyncAt(row, column, srcLine, lines) {
    markFormulaSync(srcLine, lines)
    window.dispatchEvent(new CustomEvent('cursor:editor:update', { detail: { row, column } }))
    window.setTimeout(() => {
      dispatchSyncToPdf()
    }, AFTER_CURSOR_UPDATE_MS)
  }

  function run() {
    if (!isProjectPage() || !hasPdfPane()) return

    const view = getEditorView()

    // 兜底：拿不到视图时，退化为「选中文本本身像公式就同步光标所在位置」
    if (!view) {
      const selection = window.getSelection()
      if (!selection || selection.isCollapsed) return
      const text = selection.toString()
      if (!text || text.length > MAX_SELECTION_CHARS) return
      if (!looksLikeMathText(text)) return
      markFormulaSync(null)
      dispatchSyncToPdf()
      return
    }

    if (!selectionInsideEditor(view)) return

    const { from, to } = view.state.selection.main
    if (to - from < 1 || to - from > MAX_SELECTION_CHARS) return

    const docText = view.state.doc.toString()
    const ranges = collectMathRanges(docText)

    // 选区起点在公式内，或选区覆盖了某个公式，都算命中
    const overlapping = ranges.filter(range => range.to > from && range.from < to)
    const hit =
      ranges.find(range => from >= range.from && from <= range.to) ??
      ranges.find(range => range.from < to && range.to > from)
    if (!hit) return

    const key = `${from}:${to}:${hit.from}:${hit.to}:${docText.length}`
    const now = Date.now()
    if (key === lastKey && now - lastKeyAt < SAME_SELECTION_QUIET_MS) return
    lastKey = key
    lastKeyAt = now

    // 同步目标：公式区间起点，保证高亮落在公式开头而不是拖选落点
    const target = hit.from
    const head = view.state.selection.main.head

    // 选区覆盖到的全部公式起始行（去重、按出现顺序）：一个选区里有多个公式时
    // 由 Part A2 分别请求再合并矩形，让整段选区一起高亮
    const lines = []
    for (const range of overlapping) {
      const lineNo = view.state.doc.lineAt(range.from).number
      if (lines.indexOf(lineNo) < 0) lines.push(lineNo)
    }

    if (head >= hit.from && head <= hit.to) {
      // 光标本来就在公式里，直接用官方记录的坐标同步（缓存的就是 head 位置）
      markFormulaSync(view.state.doc.lineAt(head).number, lines)
      dispatchSyncToPdf()
      return
    }

    const line = view.state.doc.lineAt(target)
    reportSelection(view, from, to, head, lines, line.number)
    dispatchSyncAt(line.number - 1, target - line.from, line.number, lines)
  }

  function scheduleRun() {
    if (timer) window.clearTimeout(timer)
    timer = window.setTimeout(() => {
      timer = null
      try {
        run()
      } catch (err) {
        console.warn('[ayaka] synctex selection failed', err)
      }
    }, DEBOUNCE_MS)
  }

  document.addEventListener('selectionchange', scheduleRun, true)
  // selectionchange 在部分情况下（拖选结束不松手）不触发，鼠标抬起再兜一次
  document.addEventListener('mouseup', scheduleRun, true)

  // 调试用：控制台手动触发一次
  window.__ayakaSyncSelectionNow = () => {
    lastKey = ''
    run()
  }
  // 自测用：暴露数学区间扫描
  window.__ayakaCollectMathRanges = collectMathRanges
})();

/* ===========================================================================
 * 高亮修正：/sync/code 响应消歧 + 行内公式 synctex 收窄 + 墨迹扫描扩框
 *
 * 实测依据（Overleaf Pro 服务端 + 前端 bundle 反解，2026-11）：
 *  - 前端请求层全部走原生 fetch；/sync/code 是 GET，响应形如
 *    { pdf: [{page,h,v,width,height}, ...], downloadedFromCache: bool }，
 *    响应的 pdf 数组被直接 setHighlights 渲染成绝对定位黄色 div
 *    （rgb(255,255,0)，opacity 0→0.5→0 淡入淡出，挂在 PDF 查看器容器上）。
 *  - 服务端只按源码行映射，column 参数不影响结果；
 *    同一矩形重复返回多次；多行环境会混入整栏宽行盒；
 *    display/align 首行只返回 height=0 的整栏宽矩形（渲染不可见）。
 *  - 行内公式的 /sync/code 响应只有整行行盒（column 也挖不出公式子盒），
 *    需要客户端解析编译产物 output.synctex.gz 里的 math 记录自行收窄。
 * =========================================================================== */
(() => {
  'use strict'

  if (window.__ayakaSyncTexFix) return
  window.__ayakaSyncTexFix = true

  // 注入版本号：宿主把 [ayaka-sync] 诊断经 postMessage 写进 PCL 日志，
  // 用来确认「跑的是哪一版脚本」以及每次同步的过滤结果
  const BRIDGE_VERSION = '10'

  // 诊断上报：控制台 + 宿主日志两条路（宿主日志在 设置 → 日志 里可导出）
  function report(text) {
    try {
      console.info(text)
    } catch {
      // ignore
    }
    try {
      const webview = window.chrome && window.chrome.webview
      if (webview && typeof webview.postMessage === 'function') {
        webview.postMessage({ type: 'ayaka:syncLog', message: text })
      }
    } catch {
      // ignore
    }
  }

  report(`[ayaka-sync] bridge v${BRIDGE_VERSION} 已注入（${typeof location !== 'undefined' ? location.pathname : '?'}）`)
  // 供第一段（选区扫描）复用同一路诊断输出
  window.__ayakaReport = report

  const EPS = 0.5
  // 容器矩形阈值：tikz／表格等大盒内容所在行会连带返回「从该行到页底」的容器盒
  // （宽度 = 栏宽，高度可达数百 bp），单条公式高亮不可能有 300bp 高（≈10.6cm）
  const CONTAINER_MIN_HEIGHT_BP = 300
  // 容器盒的第二种判据（实测 194×288bp 那条就卡在 300 以下）：
  // 高度 ≥ 200bp，且同栏存在与之重叠、高度不足它 60% 的内容盒
  const CONTAINER_OVERLAP_MIN_BP = 200
  const CONTAINER_OVERLAP_RATIO = 0.6

  function rectsOverlap(a, b) {
    const ox = Math.min((a.h || 0) + (a.width || 0), (b.h || 0) + (b.width || 0)) -
      Math.max(a.h || 0, b.h || 0)
    const oy = Math.min((a.v || 0) + (a.height || 0), (b.v || 0) + (b.height || 0)) -
      Math.max(a.v || 0, b.v || 0)
    return ox > EPS && oy > EPS
  }

  // 同一栏：横向重叠超过较窄者一半（跨栏的两条矩形横向不重叠，不算竞争）
  function sameColumn(a, b) {
    const aw = a.width || 0
    const bw = b.width || 0
    if (aw <= 0 || bw <= 0) return false
    const ox = Math.min((a.h || 0) + aw, (b.h || 0) + bw) - Math.max(a.h || 0, b.h || 0)
    return ox > 0.5 * Math.min(aw, bw)
  }

  // 竖排容器盒：同栏内被更矮的内容盒「顶住」，自己一路延伸到页底。
  // 判据要求「起点更靠下、终点更靠下」：容器从内容中部开始，一直伸到内容盒下方，
  // 这样图文并排（图盒在上、题注在下且更矮）之类的情形不会被误判成容器。
  function isColumnContainer(rect, list) {
    const height = rect.height || 0
    if (height < CONTAINER_OVERLAP_MIN_BP) return false
    const top = rect.v || 0
    const bottom = top + height
    return list.some(other => {
      if (other === rect) return false
      const otherHeight = other.height || 0
      if (otherHeight <= 0 || otherHeight > CONTAINER_OVERLAP_RATIO * height) return false
      const otherTop = other.v || 0
      if (top <= otherTop + EPS) return false
      if (bottom <= otherTop + otherHeight + EPS) return false
      if (!sameColumn(rect, other)) return false
      return rectsOverlap(rect, other)
    })
  }

  /* ------------------------------------------------------------------ *
   * A0. 纵向坐标系换算：synctex 的 v 是盒子「下沿」，前端当「上沿」用
   *
   * 实测（TeX Live 2026 + 本项目 clsi，2026-11）：
   *  - `synctex view` 返回的 v 是盒子基线（下沿），盒子纵向占 [v-height, v]：
   *    本地编译同一文档对照 PDF 墨迹，第 1 行图墨迹在 y=124..287，synctex 给
   *    v=286.59；第 2 行图墨迹在 y=287..450，synctex 给 v=449.38 —— 都落在下沿。
   *  - 前端 features/pdf-preview/util/highlights.ts 的 buildHighlightElement
   *    直接用 top=v、bottom=v+height，于是每条高亮都向下偏了自身高度：
   *    行内公式（h≈13bp）偏 ~10px，会被墨迹扩框向上补回来，看不出问题；
   *    tikzpicture（h≈162bp）就偏出一整张图 —— 用户实测「高亮在正下方」。
   *  - clsi 的 SynctexOutputParser.js 原样透传 v/H，服务端没有做换算。
   * 故在交给前端之前统一换算：top = v - height（height=0 的退化矩形不换算）。
   * 另外前端把 viewBox 高度写成 `viewBox[3] + 10`（多 10bp 的补偿常数），
   * 换算后所有高亮会整体上移 10bp（≈10px，小公式看不出、整图能看出来），
   * 这里一并加回去，让落点与内容完全对齐。
   * 几何消歧与公式收窄仍在原始（下沿）坐标系里跑，避免既有规则失效。
   * ------------------------------------------------------------------ */

  // 前端 highlights.ts: `const viewBoxHeight = viewport.viewBox[3] + 10`
  const VIEWBOX_Y_FUDGE_BP = 10

  function toTopEdge(rect) {
    const height = (rect && rect.height) || 0
    if (!rect) return rect
    const v = (rect.v || 0) + VIEWBOX_Y_FUDGE_BP
    return height > 0 ? Object.assign({}, rect, { v: v - height }) : Object.assign({}, rect, { v: v })
  }

  /* ------------------------------------------------------------------ *
   * A. /sync/code 响应几何消歧
   * ------------------------------------------------------------------ */

  function disambiguatePdfRects(raw) {
    if (!Array.isArray(raw) || raw.length === 0) return raw

    // 1) 去重（synctex 对同一矩形会重复返回多次）
    const seen = new Set()
    const rects = []
    for (const r of raw) {
      if (!r || typeof r.page !== 'number') continue
      const key =
        r.page + '|' +
        (r.h || 0).toFixed(1) + '|' + (r.v || 0).toFixed(1) + '|' +
        (r.width || 0).toFixed(1) + '|' + (r.height || 0).toFixed(1)
      if (seen.has(key)) continue
      seen.add(key)
      rects.push(r)
    }
    // 数据异常时原样返回，宁可不修也不能让高亮消失
    if (rects.length === 0) return raw

    // 2) 按页分组处理
    const byPage = new Map()
    for (const r of rects) {
      if (!byPage.has(r.page)) byPage.set(r.page, [])
      byPage.get(r.page).push(r)
    }

    const keep = []
    for (const list of byPage.values()) {
      // 退化矩形（width/height≈0，渲染不可见）默认丢弃；
      // 若整页只剩退化矩形（align 首行的典型输出），回退保留原列表，
      // 让滚动定位仍然生效，高度为 0 的问题交给墨迹扫描补救。
      const solid = list.filter(r => (r.width || 0) >= EPS && (r.height || 0) >= EPS)
      if (solid.length === 0) {
        keep.push(...list)
        continue
      }

      // 行级噪音判定：整页最宽矩形若在自身纵向范围内“装着”更窄的小矩形，
      // 说明它只是包住内容的小盒的行盒（多行环境的行级噪音），丢弃；
      // 行内公式只有行盒、没有更窄小盒时按原样保留，与官方行为一致。
      // 横向必须重叠，防止跨栏（行盒在 A 栏、碎片在 B 栏）误杀。
      const maxW = solid.reduce((m, r) => Math.max(m, r.width || 0), 0)
      const kept = solid.filter(r => {
        if ((r.width || 0) < 0.6 * maxW) return true
        return !solid.some(n => {
          if (n === r) return false
          if ((n.width || 0) > 0.6 * (r.width || 0)) return false
          if ((n.v || 0) < (r.v || 0) - EPS) return false
          if ((n.v || 0) > (r.v || 0) + (r.height || 0) + EPS) return false
          if ((n.h || 0) >= (r.h || 0) + (r.width || 0) - EPS) return false
          if ((n.h || 0) + (n.width || 0) <= (r.h || 0) + EPS) return false
          return true
        })
      })

      // 3) 容器矩形剔除（tikz／tabular 等大盒内容的典型输出）：
      //    synctex 会在内容所在行额外返回「从该行一直延伸到页底」的容器盒，
      //    宽度 = 栏宽、高度数百 bp —— 渲染出来就是一大片空白黄块
      //    （用户实测：选中 tikzpicture 代码，内容下方到页底整片被高亮）。
      //
      //    3a 绝对高度：单条公式高亮不可能有 300bp 高（≈10.6cm）；
      //    3b 重叠对比：实测容器盒不止 300bp（194×288bp 那条最典型），
      //       但同一栏里必有一条更矮的内容盒与它重叠（内容盒 194×162bp），
      //       且容器盒总是「从内容盒中部一直往下延伸」—— 高度差 1.6 倍以上即判容器。
      //       两条都是「同页还有别的实心矩形才动手」，整页只剩容器盒时保留
      //       （宁可大也不空，滚动定位不丢）。
      const tall = kept.filter(r => (r.height || 0) < CONTAINER_MIN_HEIGHT_BP)
      if (tall.length > 0) {
        const content = tall.filter(r => !isColumnContainer(r, tall))
        keep.push(...(content.length > 0 ? content : tall))
        continue
      }

      keep.push(...(kept.length > 0 ? kept : solid))
    }

    return keep.length > 0 ? keep : rects
  }

  /* ------------------------------------------------------------------ *
   * C. 行内公式收窄：客户端解析 output.synctex.gz
   *
   * 原理（synctex 文本格式实测逆向，TeXLive 2026）：
   *  - 坐标为 sp（1pt = 65536sp），PDF 矩形用 bp（1pt = 7200/7227 bp）；
   *    /sync/code 的 h/v/height 与 synctex 盒子逐字段吻合（≤0.002bp）。
   *  - `Input:<tag>:<path>` 源文件编号；`{<n>` 页开始；`$<tag>,<line>:<x>,<y>`
   *    是 math 开始/结束锚点，交替配对，同一行内得到公式的水平区间。
   *  - pmatrix 单元格（amsmath halign 的 `$##$` 模板）也会发 $ 记录，
   *    由「响应里已有更细矩形就不收窄」条件保护，不会被误用。
   *  - align/$$ 等要么没有 $ 记录、要么配对出零宽区间（按噪音丢弃）。
   *
   * 只在公式选区触发的同步（见 Part 1 的标记）且消歧后全为整行行盒时，
   * 用 math 区间把行盒水平收窄到公式本身；任何一步拿不到数据都原样返回。
   * ------------------------------------------------------------------ */

  const SP_TO_BP = 7200 / (7227 * 65536) // sp → bp
  const MIN_REGION_SP = 131072           // 2pt，窄于此的 math 区间按噪音丢弃
  const FORMULA_FLAG_TTL_MS = 2500       // 公式选区标记的有效期
  const SYNCTEX_FETCH_TIMEOUT_MS = 1200  // 收窄冷启动取 gz 的超时
  const SYNCTEX_CACHE_MAX = 3            // 按 buildId 缓存解析结果的上限

  const synctexUrlByBuild = new Map() // buildId → output.synctex.gz 下载 URL
  const synctexCache = new Map()      // buildId → Promise<解析结果 | null>
  const synctexCacheOrder = []        // LRU 顺序

  function basename(p) {
    const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'))
    return i < 0 ? p : p.slice(i + 1)
  }

  // 极简 synctex 解析：只取 Input / 页定界 / math 锚点三类记录
  function parseSynctex(text) {
    const fileTag = new Map() // basename → tag
    const regions = new Map() // 'tag:line' → [{x0, x1, y, page}]（sp 单位）
    const lines = text.split('\n')
    let page = 0
    let open = null
    for (let i = 0; i < lines.length; i++) {
      const ln = lines[i]
      const c0 = ln.charCodeAt(0)
      if (c0 === 73 /* 'I' */) {
        if (ln.startsWith('Input:')) {
          const m = /^Input:(\d+):(.+)$/.exec(ln)
          if (m) fileTag.set(basename(m[2]), +m[1])
        }
        continue
      }
      if (c0 === 123 /* '{' */) {
        const m = /^\{(\d+)\s*$/.exec(ln)
        if (m) {
          page = +m[1]
          open = null
        }
        continue
      }
      if (c0 === 36 /* '$' */) {
        const m = /^\$(\d+),(\d+):(-?\d+),(-?\d+)/.exec(ln)
        if (!m) continue
        const tag = +m[1]
        const line = +m[2]
        const x = +m[3]
        const y = +m[4]
        if (open && open.tag === tag && open.line === line) {
          // 配对成功；宽度不足 2pt 的（含 $$ 空对、跨行负宽度）丢弃
          if (x - open.x >= MIN_REGION_SP) {
            const key = tag + ':' + line
            let arr = regions.get(key)
            if (!arr) {
              arr = []
              regions.set(key, arr)
            }
            arr.push({ x0: open.x, x1: x, y: open.y, page })
          }
          open = null
        } else {
          open = { tag, line, x, y }
        }
      }
    }
    return { fileTag, regions }
  }

  async function fetchSynctexParse(buildId) {
    const url = synctexUrlByBuild.get(buildId)
    if (!url || typeof DecompressionStream !== 'function') return null
    const resp = await origFetch(url)
    if (!resp || !resp.ok) return null
    const buf = await resp.arrayBuffer()
    const text = await new Response(
      new Blob([buf]).stream().pipeThrough(new DecompressionStream('gzip'))
    ).text()
    return parseSynctex(text)
  }

  function getSynctexForBuild(buildId) {
    let p = synctexCache.get(buildId)
    if (!p) {
      p = fetchSynctexParse(buildId).catch(() => null)
      synctexCache.set(buildId, p)
      synctexCacheOrder.push(buildId)
      while (synctexCacheOrder.length > SYNCTEX_CACHE_MAX) {
        synctexCache.delete(synctexCacheOrder.shift())
      }
    }
    return p
  }

  // compile 响应里记录 synctex.gz 的下载 URL 并后台预取解析
  function noteCompileResponse(resp) {
    if (!resp || !resp.ok) return
    const type = resp.headers.get('content-type') || ''
    if (type.indexOf('json') < 0) return
    resp.clone().json().then(data => {
      if (!data || !Array.isArray(data.outputFiles)) return
      for (const f of data.outputFiles) {
        const url = f && f.url
        if (!url || typeof url !== 'string') continue
        if (String(f.path || url).indexOf('synctex.gz') < 0) continue
        const build = String(f.build || data.buildId || '')
        if (!build) continue
        synctexUrlByBuild.set(build, url)
        getSynctexForBuild(build)
        break
      }
    }).catch(() => {})
  }

  function syncQueryParam(url, name) {
    const m = new RegExp('[?&]' + name + '=([^&]*)').exec(url)
    return m ? decodeURIComponent(m[1]) : ''
  }

  // 公式选区标记是否对本次 /sync/code 生效；生效则消费掉（一次性）
  function consumeFormulaFlag(lineStr) {
    const flag = window.__ayakaFormulaSyncPending
    if (!flag) return false
    if (Date.now() - (flag.at || 0) > FORMULA_FLAG_TTL_MS) {
      try { delete window.__ayakaFormulaSyncPending } catch { }
      return false
    }
    const line = +lineStr
    if (!Number.isFinite(line)) return false
    // 请求行号与标记行号需一致（±1 容忍官方前端行基差异）
    if (flag.line != null && Math.abs(flag.line - line) > 1) return false
    try { delete window.__ayakaFormulaSyncPending } catch { }
    return true
  }

  // 行盒收窄主逻辑；不满足任何前提时原样返回输入
  async function narrowWithSynctex(rects, syncUrl) {
    if (!Array.isArray(rects) || rects.length === 0) return rects
    const lineStr = syncQueryParam(syncUrl, 'line')
    if (!consumeFormulaFlag(lineStr)) return rects

    const buildId = syncQueryParam(syncUrl, 'buildId')
    const file = syncQueryParam(syncUrl, 'file')
    if (!buildId || !file) return rects
    if (!synctexUrlByBuild.has(buildId)) return rects // 本页会话没见过这次编译

    let parsed = null
    let timerId = null
    try {
      parsed = await Promise.race([
        getSynctexForBuild(buildId),
        new Promise((resolve, reject) => {
          timerId = setTimeout(() => reject(new Error('synctex timeout')), SYNCTEX_FETCH_TIMEOUT_MS)
        })
      ])
    } catch {
      return rects
    } finally {
      clearTimeout(timerId)
    }
    if (!parsed) return rects

    const tag = parsed.fileTag.get(basename(file))
    if (!tag) return rects
    const regions = parsed.regions.get(tag + ':' + lineStr)
    if (!regions || regions.length === 0) return rects

    // 响应里已有更细矩形（多行环境/碎片盒）说明服务端本来就精确，不收窄
    const maxW = rects.reduce((m, r) => Math.max(m, r.width || 0), 0)
    if (rects.some(r => (r.width || 0) < 0.6 * maxW)) return rects

    const narrowed = []
    for (const row of rects) {
      if (!row || typeof row.page !== 'number') continue
      const rx0 = row.h || 0
      const rx1 = rx0 + (row.width || 0)
      for (const rg of regions) {
        if (rg.page !== row.page) continue
        const gx0 = rg.x0 * SP_TO_BP
        const gx1 = rg.x1 * SP_TO_BP
        const gy = rg.y * SP_TO_BP
        // 公式基线落在行盒纵向范围内，且与行盒横向有交集
        if (gy < (row.v || 0) - (row.height || 0) - EPS) continue
        if (gy > (row.v || 0) + EPS) continue
        if (gx1 < rx0 + EPS || gx0 > rx1 - EPS) continue
        narrowed.push({
          page: row.page,
          h: gx0,
          v: row.v,
          width: gx1 - gx0,
          height: row.height
        })
      }
    }
    return narrowed.length > 0 ? narrowed : rects
  }

  const SYNC_CODE_URL_RE = /\/sync\/code\?/
  const COMPILE_URL_RE = /\/project\/[^/?]+\/compile/

  // 诊断（可在启动器日志里看到，便于排查“高亮不对”的现场）：
  // 只输出 [ayaka-sync] 前缀，宿主 WorkbenchWindow 会把它们转发进 PCL 日志。
  function briefRects(list) {
    const seen = new Set()
    const out = []
    for (const r of list) {
      if (!r || typeof r.page !== 'number') continue
      const s = `${r.page}:${(r.h || 0).toFixed(0)},${(r.v || 0).toFixed(0)} ` +
        `${(r.width || 0).toFixed(0)}x${(r.height || 0).toFixed(0)}`
      if (seen.has(s)) continue
      seen.add(s)
      out.push(s)
    }
    return out.join(' | ')
  }

  function logSync(url, raw, out) {
    try {
      report(
        `[ayaka-sync] ${syncQueryParam(url, 'file')}:${syncQueryParam(url, 'line')} ` +
        `原始 ${raw.length} 条 → 过滤 ${out.length} 条 | ` +
        `raw: ${briefRects(raw)} | out: ${briefRects(out)}`
      )
    } catch {
      // 诊断日志绝不能影响同步
    }
  }

  function requestUrlOf(input) {
    if (typeof input === 'string') return input
    if (input && typeof input.url === 'string') return input.url
    return ''
  }

  // 公式选区触发的同步：把请求里的 line 强制改成公式起点所在行。
  // 官方 syncToPdf 用的是「缓存的光标位置」，而编辑器自身的 cursor 更新会和
  // 我们伪造的事件抢（实测极端情况下最终取选区末端：选中前两张图只高亮第二张）。
  // 直接在 URL 上定死行号，行为与点击公式开头完全一致，不再依赖事件顺序。
  function forcedSyncUrl(url) {
    const flag = window.__ayakaFormulaSyncPending
    if (!flag || flag.line == null) return ''
    if (Date.now() - (flag.at || 0) > FORMULA_FLAG_TTL_MS) return ''
    const current = syncQueryParam(url, 'line')
    if (current && +current === flag.line) return '' // 已经是目标行
    return url.replace(/([?&]line=)[^&]*/, '$1' + flag.line)
  }

  /* ------------------------------------------------------------------ *
   * A2. 一个选区命中多个公式时合并高亮
   *
   * 官方 setHighlights(data.pdf) 是「整批替换」，一次同步只能显示一组矩形，
   * 所以选中多张 tikzpicture / 多个公式时只能亮其中一个。这里对选区内的其余
   * 公式各自补发一次 /sync/code（同 file/buildId/editorId，只换 line），把
   * 返回的矩形并进首个响应，前端一次渲染就会全部高亮。
   * 失败/超时一律降级为「只亮首个公式」，不影响主流程。
   * ------------------------------------------------------------------ */

  const MULTI_SYNC_TIMEOUT_MS = 1500

  // 取走本次要合并的其它公式行号（一次性；首个请求行本身不重复请求）
  function takeExtraSyncLines(url) {
    const flag = window.__ayakaFormulaSyncPending
    if (!flag || !Array.isArray(flag.lines) || flag.lines.length < 2) return []
    const primary = +syncQueryParam(url, 'line')
    return flag.lines.filter(line => line !== primary)
  }

  function clearFormulaFlag() {
    try {
      delete window.__ayakaFormulaSyncPending
    } catch {
      // ignore
    }
  }

  function fetchSyncRects(url, line) {
    const target = url.replace(/([?&]line=)[^&]*/, '$1' + line)
    return origFetch(target, { credentials: 'same-origin' })
      .then(resp => {
        if (!resp || !resp.ok) return []
        return resp.json().then(data => (data && Array.isArray(data.pdf) ? data.pdf : []))
      })
      .catch(() => [])
  }

  function mergeExtraSyncRects(url, primaryRects, extraLines) {
    if (extraLines.length === 0) return Promise.resolve(primaryRects)
    const all = Promise.all(extraLines.map(line => fetchSyncRects(url, line)))
    const timeout = new Promise(resolve => {
      window.setTimeout(() => resolve(null), MULTI_SYNC_TIMEOUT_MS)
    })
    return Promise.race([all, timeout]).then(parts => {
      if (!parts) return primaryRects // 超时：只用首个公式的矩形
      let merged = primaryRects.slice()
      for (const part of parts) {
        if (Array.isArray(part) && part.length > 0) merged = merged.concat(part)
      }
      return merged
    })
  }

  // 包装 window.fetch：
  //  - /sync/code 命中时先按公式选区改写 line，再消歧 + （选区触发的）行内公式收窄；
  //  - compile 响应只旁路记录 synctex.gz URL，本身原样透传；
  //  - 其余请求原样透传。
  const origFetch = window.fetch
  if (typeof origFetch === 'function') {
    window.fetch = function (input, init) {
      let url = requestUrlOf(input)
      let requestInput = input
      if (SYNC_CODE_URL_RE.test(url)) {
        const forced = forcedSyncUrl(url)
        if (forced && forced !== url) {
          url = forced
          requestInput = forced
        }
      }
      const isSync = SYNC_CODE_URL_RE.test(url)
      const isCompile =
        !isSync &&
        COMPILE_URL_RE.test(url) &&
        init &&
        init.method &&
        String(init.method).toUpperCase() === 'POST'

      const promise = origFetch.call(this, requestInput, init)
      if (isCompile) {
        promise.then(noteCompileResponse).catch(() => {})
      }
      if (!isSync) return promise

      return promise.then(resp => {
        if (!resp || !resp.ok) return resp
        const type = resp.headers.get('content-type') || ''
        if (type.indexOf('json') < 0) return resp

        // clone 之后读，失败时仍能把原响应交还给应用
        const clone = resp.clone()
        return clone.json().then(async data => {
          if (!data || !Array.isArray(data.pdf)) return resp
          // 选区命中多个公式：先把其余公式的矩形并进来，再统一消歧/换算
          const extraLines = takeExtraSyncLines(url)
          if (extraLines.length > 0) clearFormulaFlag()
          let merged = data.pdf
          if (extraLines.length > 0) {
            merged = await mergeExtraSyncRects(url, data.pdf, extraLines)
            report(
              `[ayaka-sync] 多公式合并 ${extraLines.length + 1} 处 ` +
              `(line ${[syncQueryParam(url, 'line')].concat(extraLines).join(',')}) → ${merged.length} 条`
            )
          }
          let filtered = disambiguatePdfRects(merged)
          // 多公式合并时不收窄：收窄只对单个公式的 math 锚点成立
          if (extraLines.length === 0) filtered = await narrowWithSynctex(filtered, url)
          // 最后一步：v 换算成「上沿」再交给前端（见 toTopEdge 注释）
          const output = filtered.map(toTopEdge)
          logSync(url, merged, output)
          if (output.length === 0) return resp

          const headers = new Headers(resp.headers)
          headers.delete('content-length')
          headers.delete('content-encoding')
          const body = JSON.stringify(Object.assign({}, data, { pdf: output }))
          return new Response(body, {
            status: resp.status,
            statusText: resp.statusText,
            headers
          })
        }).catch(err => {
          // 绝不静默：过滤链一旦抛错就会回退原始响应（高亮维持官方行为），
          // 这条日志是判断“为什么没生效”的唯一线索
          report('[ayaka-sync] 过滤链异常，回退原始响应：' + (err && err.message ? err.message : err))
          return resp
        })
      })
    }
  }

  /* ------------------------------------------------------------------ *
   * B. 高亮元素墨迹扫描扩框
   * ------------------------------------------------------------------ */

  const HIGHLIGHT_BG = 'rgb(255, 255, 0)'
  const processedHighlights = new WeakSet()
  // 高亮 div → 'ink'（区域内确有墨迹）/ 'blank'（区域内全空白）
  const highlightInk = new WeakMap()
  // 当前存活的高亮 div（判「同页是否还有正确高亮」用；量级很小）
  const liveHighlights = new Set()
  // 空白判定的重试次数：画布可能还在渲染（首屏/翻页）
  const BLANK_RETRY = 3
  const PENDING_RETRY = 5
  const RETRY_MS = 350

  function isHighlightNode(node) {
    if (!node || node.nodeType !== 1) return false
    const style = node.style
    if (!style) return false
    return (
      style.backgroundColor === HIGHLIGHT_BG &&
      style.position === 'absolute' &&
      style.pointerEvents === 'none'
    )
  }

  // 高亮 div 挂在查看器容器上，按几何位置就近找到对应页面的 canvas
  function findCanvasFor(divRect) {
    const cy = divRect.top + Math.max(divRect.height, 2) / 2
    let best = null
    let bestDist = Infinity
    for (const page of document.querySelectorAll('.pdfViewer .page')) {
      const canvas = page.querySelector('canvas')
      if (!canvas) continue
      const pr = canvas.getBoundingClientRect()
      if (pr.width === 0) continue
      let dist = 0
      if (cy < pr.top) dist = pr.top - cy
      else if (cy > pr.bottom) dist = cy - pr.bottom
      if (dist < bestDist) {
        bestDist = dist
        best = canvas
      }
    }
    return best
  }

  // 画布是否已经真正绘制过内容：空白矩形的判定前提。
  // 抽 24 条横向扫描线粗扫，避免整页 getImageData 的开销。
  function canvasIsPainted(canvas) {
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) return false
    const w = canvas.width
    const h = canvas.height
    if (!w || !h) return false
    const strips = 24
    for (let i = 0; i < strips; i++) {
      const y = Math.min(h - 1, Math.floor((i + 0.5) * h / strips))
      let row
      try {
        row = ctx.getImageData(0, y, w, 1).data
      } catch {
        return false
      }
      for (let x = 0; x < w; x += 2) {
        const p = x * 4
        if (row[p + 3] >= 16 && !(row[p] >= 245 && row[p + 1] >= 245 && row[p + 2] >= 245)) return true
      }
    }
    return false
  }

  // 扫描结果：
  //   'ink'     —— 区域内确有墨迹（可能已扩框）
  //   'blank'   —— 画布已渲染，但区域整片空白：这条高亮是错位的噪音矩形
  //   'pending' —— 画布未就绪／坐标取不到，稍后重试
  function scanAndExpand(div) {
    const divRect = div.getBoundingClientRect()
    const canvas = findCanvasFor(divRect)
    if (!canvas) return 'pending'
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) return 'pending'

    const canvasRect = canvas.getBoundingClientRect()
    const scale = canvas.width / canvasRect.width
    if (!Number.isFinite(scale) || scale <= 0) return 'pending'

    // div → canvas 设备像素坐标
    let x0 = Math.round((divRect.left - canvasRect.left) * scale)
    let x1 = Math.round((divRect.right - canvasRect.left) * scale)
    let y0 = Math.round((divRect.top - canvasRect.top) * scale)
    let y1 = Math.round((divRect.bottom - canvasRect.top) * scale)
    // 原始矩形（对应 div 当前的内联样式）：收窄/扩框后要按它算样式增量
    const ox0 = x0
    const ox1 = x1
    const oy0 = y0
    const oy1 = y1

    // 扫描参数（设备像素）：
    //   mX/mY：允许扩框的最大边距，只留抗锯齿余量（实测取大值会把相邻内容算进来）；
    //   mSeedY：零高度矩形的种子搜索半径，需要比 mY 大得多才能找到最近一行墨迹；
    //   gapX/gapY：容忍的空隙小于行距/栏间距，不吞相邻行、不跨栏。
    const mX = Math.round(6 * scale)
    const mY = Math.round(8 * scale)
    const mSeedY = Math.round(44 * scale)
    const gapX = Math.max(3, Math.round(5 * scale))
    const gapY = Math.max(2, Math.round(2.2 * scale))

    const rx0 = Math.max(0, x0 - mX)
    const rx1 = Math.min(canvas.width, x1 + mX)
    // 纵向取样窗口要容纳种子搜索半径（零高度矩形需要更远的搜索范围）
    const ry0 = Math.max(0, y0 - Math.max(mY, mSeedY))
    const ry1 = Math.min(canvas.height, y1 + Math.max(mY, mSeedY))
    if (rx1 - rx0 < 2 || ry1 - ry0 < 2) return 'pending'

    let img
    try {
      img = ctx.getImageData(rx0, ry0, rx1 - rx0, ry1 - ry0)
    } catch {
      return 'pending'
    }
    const W = img.width
    const D = img.data

    // 「墨迹」判定：偏白（三通道都很亮）与透明都算空白。
    // 阈值取 235 而不是 245 —— 245 会把画布抗锯齿的浅灰边缘当成墨迹，
    // 收窄时收不下去、扩框时又往外长；淡彩（pgfplots 的 pink/green 等）仍能识别。
    const INK_LEVEL = 235
    const isBlank = (x, y) => {
      const p = ((y - ry0) * W + (x - rx0)) * 4
      if (D[p + 3] < 16) return true
      return D[p] >= INK_LEVEL && D[p + 1] >= INK_LEVEL && D[p + 2] >= INK_LEVEL
    }
    // 行/列判定要求至少 2 个墨迹像素：单个孤立噪点（PDF 抗锯齿、压缩噪点）
    // 不该决定边界。窗口很窄时退化为 1，避免细竖线判不出来。
    const inkNeed = (span) => (span >= 6 ? 2 : 1)
    const rowHasInk = (y, ax0, ax1) => {
      const need = inkNeed(ax1 - ax0)
      let hit = 0
      for (let x = ax0; x < ax1; x++) if (!isBlank(x, y) && ++hit >= need) return true
      return false
    }
    const colHasInk = (x, ay0, ay1) => {
      const need = inkNeed(ay1 - ay0)
      let hit = 0
      for (let y = ay0; y < ay1; y++) if (!isBlank(x, y) && ++hit >= need) return true
      return false
    }

    // 零高度矩形（display/align 首行的 height=0 输出）：
    // 从原位置上下搜索第一行墨迹作为种子，救回不可见高亮
    if (y1 - y0 < 2) {
      const sx0 = Math.max(rx0, x0)
      const sx1 = Math.min(rx1, x1)
      let seed = -1
      for (let dy = 0; dy <= mSeedY && seed < 0; dy++) {
        if (y0 - dy >= ry0 && rowHasInk(y0 - dy, sx0, sx1)) seed = y0 - dy
        else if (y0 + dy < ry1 && rowHasInk(y0 + dy, sx0, sx1)) seed = y0 + dy
      }
      if (seed < 0) return 'pending'
      y0 = seed
      y1 = seed + 1
    } else {
      // 原矩形内部必须有墨迹：没有墨迹有两种可能 —— 画布还没渲染（重试），
      // 或这条矩形本身就是错位的空白噪音（整片空白的高亮块，丢掉）。
      let hasInk = false
      const sy0 = Math.max(ry0, y0)
      const sy1 = Math.min(ry1, y1)
      const step = Math.max(1, Math.floor((sy1 - sy0) / 24))
      outer: for (let y = sy0; y < sy1; y += step) {
        for (let x = Math.max(rx0, x0); x < Math.min(rx1, x1); x++) {
          if (!isBlank(x, y)) { hasInk = true; break outer }
        }
      }
      if (!hasInk) return canvasIsPainted(canvas) ? 'blank' : 'pending'
    }

    // 收窄：synctex 盒子常带大量留白（tikzpicture 的坐标轴外边距、行盒的列宽），
    // 直接高亮会把空白一起框进去。先把矩形收缩到自身范围内的墨迹边界，
    // 之后再走下面的扩框逻辑（此时通常已经贴住内容，不会再扩）。
    // 只收缩、不越过原矩形：避免把错误位置的矩形收缩成别处的墨点。
    let ty0 = y0
    while (ty0 < y1 - 1 && !rowHasInk(ty0, x0, x1)) ty0++
    let ty1 = y1
    while (ty1 > ty0 + 1 && !rowHasInk(ty1 - 1, x0, x1)) ty1--
    let tx0 = x0
    while (tx0 < x1 - 1 && !colHasInk(tx0, ty0, ty1)) tx0++
    let tx1 = x1
    while (tx1 > tx0 + 1 && !colHasInk(tx1 - 1, ty0, ty1)) tx1--
    x0 = tx0
    x1 = tx1
    y0 = ty0
    y1 = ty1

    // 收窄成功说明内容整体落在矩形内部，不需要再往外扩（扩了就是多余的边距）
    const trimmed = tx0 !== ox0 || tx1 !== ox1 || ty0 !== oy0 || ty1 !== oy1
    if (!trimmed) {
      // 以下扩框只对「矩形本身切进了内容」的情况生效（高阶分式/矩阵纵向溢出、
      // 行盒高度不足等）：向墨迹方向补最多 mX/mY。
      const limitTop = Math.max(ry0, y0 - mY)
      const limitBottom = Math.min(ry1, y1 + mY)
      let ny0 = y0
      let gap = 0
      for (let y = y0 - 1; y >= limitTop; y--) {
        if (rowHasInk(y, x0, x1)) { ny0 = y; gap = 0; continue }
        if (++gap > gapY) break
      }
      let ny1 = y1
      gap = 0
      for (let y = y1; y < limitBottom; y++) {
        if (rowHasInk(y, x0, x1)) { ny1 = y + 1; gap = 0; continue }
        if (++gap > gapY) break
      }
      let nx0 = x0
      gap = 0
      for (let x = x0 - 1; x >= rx0; x--) {
        if (colHasInk(x, ny0, ny1)) { nx0 = x; gap = 0; continue }
        if (++gap > gapX) break
      }
      let nx1 = x1
      gap = 0
      for (let x = x1; x < rx1; x++) {
        if (colHasInk(x, ny0, ny1)) { nx1 = x + 1; gap = 0; continue }
        if (++gap > gapX) break
      }
      x0 = nx0
      x1 = nx1
      y0 = ny0
      y1 = ny1
    }

    // 与原始矩形比较：收窄（缩小）和扩框（放大）都要落到样式上
    if (x0 === ox0 && x1 === ox1 && y0 === oy0 && y1 === oy1) return 'ink'

    // 换算回 div 的 CSS 像素并改写内联样式（沿用官方容器的坐标系；
    // 增量一律相对原始矩形计算，与 div 当前样式对应）
    const dLeft = (x0 - ox0) / scale
    const dTop = (y0 - oy0) / scale
    const dWidth = (x1 - x0 - (ox1 - ox0)) / scale
    const dHeight = (y1 - y0 - (oy1 - oy0)) / scale
    const left = parseFloat(div.style.left)
    const top = parseFloat(div.style.top)
    const width = parseFloat(div.style.width)
    const height = parseFloat(div.style.height)
    if (![left, top, width, height].every(Number.isFinite)) return 'pending'

    // 取整用 round：floor 会系统性少 1px（右下边被切掉半像素），
    // 对一行公式/一张图这种小目标肉眼可辨
    div.style.left = Math.round(left + dLeft) + 'px'
    div.style.top = Math.round(top + dTop) + 'px'
    div.style.width = Math.max(1, Math.round(width + dWidth)) + 'px'
    div.style.height = Math.max(1, Math.round(height + dHeight)) + 'px'
    return 'ink'
  }

  /* ------------------------------------------------------------------ *
   * D. 空白高亮剔除
   *
   * tikz／表格等大盒内容所在行，/sync/code 除了内容盒还会返回错位的空白矩形
   * （用户实测：选中 tikzpicture 代码，右栏从内容下方到页底整片空白被高亮）。
   * 这类矩形在画布上一点墨迹都没有 —— 空白即噪音，不应停留。
   *
   * 只在「同页另有含墨迹的高亮」时删除：整页唯一高亮即使空白也保留，
   * 保证滚动定位与可见性行为不退化。
   * ------------------------------------------------------------------ */

  function pageOfHighlight(div) {
    const canvas = findCanvasFor(div.getBoundingClientRect())
    if (!canvas) return null
    return canvas.closest('.pdfViewer .page') || canvas
  }

  function forgetHighlight(div) {
    liveHighlights.delete(div)
  }

  function dropHighlight(div) {
    try {
      div.remove()
    } catch {
      div.style.display = 'none'
    }
    forgetHighlight(div)
  }

  // 同页是否已有含墨迹的高亮
  function hasInkSibling(div) {
    const page = pageOfHighlight(div)
    if (!page) return false
    for (const other of liveHighlights) {
      if (other === div) continue
      if (!other.isConnected) {
        forgetHighlight(other)
        continue
      }
      if (highlightInk.get(other) !== 'ink') continue
      if (pageOfHighlight(other) === page) return true
    }
    return false
  }

  // 本条确认为空白：同页有正确高亮才删
  function pruneBlankHighlight(div) {
    if (!hasInkSibling(div)) return false
    dropHighlight(div)
    return true
  }

  // 本条确认含墨迹：回收之前被判空白的同页高亮
  function pruneBlankSiblings(div) {
    const page = pageOfHighlight(div)
    if (!page) return
    for (const other of [...liveHighlights]) {
      if (other === div) continue
      if (!other.isConnected) {
        forgetHighlight(other)
        continue
      }
      if (highlightInk.get(other) !== 'blank') continue
      if (pageOfHighlight(other) !== page) continue
      dropHighlight(other)
    }
  }

  // 高亮几何诊断：确认「服务端给的 v」与「实际落点」是否一致
  function logHighlight(div, status) {
    try {
      const rect = div.getBoundingClientRect()
      const canvas = findCanvasFor(rect)
      const pageEl = canvas ? canvas.closest('.pdfViewer .page') || canvas : null
      const pageRect = pageEl ? pageEl.getBoundingClientRect() : null
      // 相对页面尺寸的百分比：截图缩放/窗口大小变化后仍可对照
      let rel = ''
      if (pageRect && pageRect.width > 0 && pageRect.height > 0) {
        const px = (n) => Math.round(n * 100) + '%'
        rel = ` rel(${px((rect.left - pageRect.left) / pageRect.width)},` +
          `${px((rect.top - pageRect.top) / pageRect.height)} ` +
          `${px(rect.width / pageRect.width)}x${px(rect.height / pageRect.height)})`
      }
      report(
        `[ayaka-sync] hl ${status} style(${div.style.left}/${div.style.top} ` +
        `${div.style.width}x${div.style.height}) abs(${Math.round(rect.left)},${Math.round(rect.top)} ` +
        `${Math.round(rect.width)}x${Math.round(rect.height)}) page(${pageEl ? pageEl.getAttribute('data-page-number') || '?' : '?'} ` +
        `${pageRect ? Math.round(pageRect.width) + 'x' + Math.round(pageRect.height) : '-'}) ` +
        `canvas(${canvas ? canvas.width + 'x' + canvas.height : '-'})${rel}`
      )
    } catch {
      // 诊断失败不影响行为
    }
  }

  function processHighlight(div) {
    if (processedHighlights.has(div)) return
    liveHighlights.add(div)
    let tries = 0
    let blankTries = 0
    const attempt = () => {
      if (!div.isConnected) {
        forgetHighlight(div)
        return
      }
      tries++
      let status = 'pending'
      try {
        status = scanAndExpand(div)
      } catch {
        status = 'pending'
      }
      if (status === 'ink') {
        processedHighlights.add(div)
        highlightInk.set(div, 'ink')
        logHighlight(div, 'ink')
        pruneBlankSiblings(div)
        return
      }
      if (status === 'blank') {
        blankTries++
        // 画布渲染有间隔：先用几次重试排除「还没画出来」
        if (blankTries < BLANK_RETRY) {
          window.setTimeout(attempt, RETRY_MS)
          return
        }
        processedHighlights.add(div)
        highlightInk.set(div, 'blank')
        logHighlight(div, 'blank')
        pruneBlankHighlight(div)
        return
      }
      // 画布可能还没渲染/还没翻到，稍后重试几次
      if (tries < PENDING_RETRY) window.setTimeout(attempt, RETRY_MS)
      else forgetHighlight(div)
    }
    attempt()
  }

  const highlightObserver = new MutationObserver(mutations => {
    try {
      for (const m of mutations) {
        for (const node of m.addedNodes) {
          if (isHighlightNode(node)) processHighlight(node)
        }
      }
    } catch {
      // 观察器自身绝不能把页面搞挂
    }
  })

  const observeHighlight = () => {
    highlightObserver.observe(document.documentElement, {
      childList: true,
      subtree: true
    })
  }
  if (document.documentElement) {
    observeHighlight()
  } else {
    document.addEventListener('readystatechange', observeHighlight, { once: true })
  }

  // 调试用：控制台手动对当前所有高亮框重跑一次墨迹扫描
  window.__ayakaRescanHighlights = () => {
    document.querySelectorAll('div').forEach(node => {
      if (isHighlightNode(node)) {
        processedHighlights.delete(node)
        processHighlight(node)
      }
    })
  }
  // 调试/自测用：暴露消歧函数
  window.__ayakaDisambiguate = disambiguatePdfRects
  // 调试/自测用：暴露纵向换算
  window.__ayakaToTopEdge = toTopEdge
  // 调试/自测用：暴露请求行号改写
  window.__ayakaForcedSyncUrl = forcedSyncUrl
  // 调试/自测用：暴露多公式合并
  window.__ayakaTakeExtraLines = takeExtraSyncLines
  window.__ayakaMergeRects = mergeExtraSyncRects
  // 调试/自测用：暴露 synctex 解析、compile 响应记录与收窄函数
  window.__ayakaParseSynctex = parseSynctex
  window.__ayakaNoteCompile = noteCompileResponse
  window.__ayakaNarrow = narrowWithSynctex
})()
