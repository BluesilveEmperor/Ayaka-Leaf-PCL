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
 *   3. 直接把光标位置对准公式起点，派发官方的 'cursor:editor:syncToPdf' 事件，
 *      交给 Overleaf 自带的 use-synctex 完成 /sync/code 请求、画高亮框、滚动定位。
 *      缩放、滚动、分页、清缓存等行为与点工具栏按钮完全一致。
 *
 * 第二段（高亮修正，由 /sync/code 实测数据驱动）：
 *   A. 拦截 /sync/code 响应做几何消歧，修复多栏误亮：
 *      - 同一矩形会被 synctex 重复返回 5~36 次 → 去重；
 *      - display/align 会产生 height=0 的整栏宽退化矩形（高亮不可见）→ 丢弃，
 *        整页只剩退化矩形时回退保留（保住 scrollIntoView 滚动行为）；
 *      - 多行环境（align 后续行等）同时返回「整栏宽行盒」和「内容级小盒」，
 *        行盒顶部范围内若落有更窄的小盒，说明它是行级噪音 → 丢弃；
 *      - 行内公式只返回行盒（synctex 无更细粒度）→ 原样保留，行为与官方一致。
 *   B. MutationObserver 监听高亮元素出现，用 canvas 墨迹扫描扩框，修复
 *      「高亮框框不住内容」（高分式/矩阵纵向溢出、align 首行 height=0 不可见）：
 *      - 在矩形四周容忍少量空白向墨迹方向扩张，覆盖完整公式墨迹；
 *      - 零高度矩形从原位置上下搜索第一行墨迹作种子（救回 align 首行）；
 *      - 垂直空隙容忍度小于行距（不吞并相邻行），水平容忍度小于栏间距
 *        （不跨栏）；只在扩得出去时改写样式，画布未渲染时自动重试。
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
          ranges.push({ from: entry.start, to: end, kind: entry.kind, name: entry.name })
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

  // 把光标位置挪到公式起点：官方 syncToPdf 读的是 useSynctex 里缓存的坐标，
  // 所以先派发一次 cursor:editor:update，等 React 写回 ref 再触发同步。
  function dispatchSyncAt(row, column) {
    window.dispatchEvent(new CustomEvent('cursor:editor:update', { detail: { row, column } }))
    window.setTimeout(dispatchSyncToPdf, AFTER_CURSOR_UPDATE_MS)
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
      dispatchSyncToPdf()
      return
    }

    if (!selectionInsideEditor(view)) return

    const { from, to } = view.state.selection.main
    if (to - from < 1 || to - from > MAX_SELECTION_CHARS) return

    const docText = view.state.doc.toString()
    const ranges = collectMathRanges(docText)

    // 选区起点在公式内，或选区覆盖了某个公式，都算命中
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

    if (head >= hit.from && head <= hit.to) {
      // 光标本来就在公式里，直接用官方记录的坐标同步
      dispatchSyncToPdf()
      return
    }

    const line = view.state.doc.lineAt(target)
    dispatchSyncAt(line.number - 1, target - line.from)
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
})();

/* ===========================================================================
 * 高亮修正：/sync/code 响应消歧 + 高亮元素墨迹扫描扩框
 *
 * 实测依据（Overleaf Pro 服务端 + 前端 bundle 反解，2026-11）：
 *  - 前端请求层全部走原生 fetch；/sync/code 是 GET，响应形如
 *    { pdf: [{page,h,v,width,height}, ...], downloadedFromCache: bool }，
 *    响应的 pdf 数组被直接 setHighlights 渲染成绝对定位黄色 div
 *    （rgb(255,255,0)，opacity 0→0.5→0 淡入淡出，挂在 PDF 查看器容器上）。
 *  - 服务端只按源码行映射，column 参数不影响结果；
 *  - 同一矩形重复返回多次；多行环境会混入整栏宽行盒；
 *    display/align 首行只返回 height=0 的整栏宽矩形（渲染不可见）。
 * =========================================================================== */
(() => {
  'use strict'

  if (window.__ayakaSyncTexFix) return
  window.__ayakaSyncTexFix = true

  const EPS = 0.5

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

      keep.push(...(kept.length > 0 ? kept : solid))
    }

    return keep.length > 0 ? keep : rects
  }

  const SYNC_CODE_URL_RE = /\/sync\/code\?/

  function requestUrlOf(input) {
    if (typeof input === 'string') return input
    if (input && typeof input.url === 'string') return input.url
    return ''
  }

  // 包装 window.fetch：只在 /sync/code 命中时改写响应体，其余原样透传
  const origFetch = window.fetch
  if (typeof origFetch === 'function') {
    window.fetch = function () {
      const promise = origFetch.apply(this, arguments)
      if (!SYNC_CODE_URL_RE.test(requestUrlOf(arguments[0]))) return promise

      return promise.then(resp => {
        if (!resp || !resp.ok) return resp
        const type = resp.headers.get('content-type') || ''
        if (type.indexOf('json') < 0) return resp

        // clone 之后读，失败时仍能把原响应交还给应用
        const clone = resp.clone()
        return clone.json().then(data => {
          if (!data || !Array.isArray(data.pdf)) return resp
          const filtered = disambiguatePdfRects(data.pdf)
          if (filtered === data.pdf) return resp

          const headers = new Headers(resp.headers)
          headers.delete('content-length')
          headers.delete('content-encoding')
          const body = JSON.stringify(Object.assign({}, data, { pdf: filtered }))
          return new Response(body, {
            status: resp.status,
            statusText: resp.statusText,
            headers
          })
        }).catch(() => resp)
      })
    }
  }

  /* ------------------------------------------------------------------ *
   * B. 高亮元素墨迹扫描扩框
   * ------------------------------------------------------------------ */

  const HIGHLIGHT_BG = 'rgb(255, 255, 0)'
  const processedHighlights = new WeakSet()

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

  function scanAndExpand(div) {
    const divRect = div.getBoundingClientRect()
    const canvas = findCanvasFor(divRect)
    if (!canvas) return false
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) return false

    const canvasRect = canvas.getBoundingClientRect()
    const scale = canvas.width / canvasRect.width
    if (!Number.isFinite(scale) || scale <= 0) return false

    // div → canvas 设备像素坐标
    const x0 = Math.round((divRect.left - canvasRect.left) * scale)
    const x1 = Math.round((divRect.right - canvasRect.left) * scale)
    let y0 = Math.round((divRect.top - canvasRect.top) * scale)
    let y1 = Math.round((divRect.bottom - canvasRect.top) * scale)

    // 扫描参数（设备像素）：
    //   垂直空隙容忍 < 行距（不吞相邻行）；水平空隙容忍 < 栏间距（不跨栏）
    const mX = Math.round(28 * scale)
    const mY = Math.round(44 * scale)
    const gapX = Math.max(3, Math.round(5 * scale))
    const gapY = Math.max(2, Math.round(2.2 * scale))

    const rx0 = Math.max(0, x0 - mX)
    const rx1 = Math.min(canvas.width, x1 + mX)
    const ry0 = Math.max(0, y0 - mY)
    const ry1 = Math.min(canvas.height, y1 + mY)
    if (rx1 - rx0 < 2 || ry1 - ry0 < 2) return false

    let img
    try {
      img = ctx.getImageData(rx0, ry0, rx1 - rx0, ry1 - ry0)
    } catch {
      return false
    }
    const W = img.width
    const D = img.data

    const isBlank = (x, y) => {
      const p = ((y - ry0) * W + (x - rx0)) * 4
      return D[p + 3] < 16 || (D[p] >= 245 && D[p + 1] >= 245 && D[p + 2] >= 245)
    }
    const rowHasInk = (y, ax0, ax1) => {
      for (let x = ax0; x < ax1; x++) if (!isBlank(x, y)) return true
      return false
    }
    const colHasInk = (x, ay0, ay1) => {
      for (let y = ay0; y < ay1; y++) if (!isBlank(x, y)) return true
      return false
    }

    // 零高度矩形（display/align 首行的 height=0 输出）：
    // 从原位置上下搜索第一行墨迹作为种子，救回不可见高亮
    if (y1 - y0 < 2) {
      const sx0 = Math.max(rx0, x0)
      const sx1 = Math.min(rx1, x1)
      let seed = -1
      for (let dy = 0; dy <= mY && seed < 0; dy++) {
        if (y0 - dy >= ry0 && rowHasInk(y0 - dy, sx0, sx1)) seed = y0 - dy
        else if (y0 + dy < ry1 && rowHasInk(y0 + dy, sx0, sx1)) seed = y0 + dy
      }
      if (seed < 0) return false
      y0 = seed
      y1 = seed + 1
    } else {
      // 原矩形内部必须有墨迹，否则多半是画布还没渲染出来，交给重试
      let hasInk = false
      const sy0 = Math.max(ry0, y0)
      const sy1 = Math.min(ry1, y1)
      const step = Math.max(1, Math.floor((sy1 - sy0) / 24))
      outer: for (let y = sy0; y < sy1; y += step) {
        for (let x = Math.max(rx0, x0); x < Math.min(rx1, x1); x++) {
          if (!isBlank(x, y)) { hasInk = true; break outer }
        }
      }
      if (!hasInk) return false
    }

    // 向上/向下扩张到墨迹边界（容忍 gapY 空白行）
    let ny0 = y0
    let gap = 0
    for (let y = y0 - 1; y >= ry0; y--) {
      if (rowHasInk(y, x0, x1)) { ny0 = y; gap = 0; continue }
      if (++gap > gapY) break
    }
    let ny1 = y1
    gap = 0
    for (let y = y1; y < ry1; y++) {
      if (rowHasInk(y, x0, x1)) { ny1 = y + 1; gap = 0; continue }
      if (++gap > gapY) break
    }

    // 向左/向右扩张（用扩张后的纵向范围；整栏宽行盒两侧是空白栏沟，天然不会扩出去）
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

    if (nx0 === x0 && nx1 === x1 && ny0 === y0 && ny1 === y1) return true

    // 换算回 div 的 CSS 像素并改写内联样式（沿用官方容器的坐标系）
    const dLeft = (nx0 - x0) / scale
    const dTop = (ny0 - y0) / scale
    const dWidth = (nx1 - nx0 - (x1 - x0)) / scale
    const dHeight = (ny1 - ny0 - (y1 - y0)) / scale
    const left = parseFloat(div.style.left)
    const top = parseFloat(div.style.top)
    const width = parseFloat(div.style.width)
    const height = parseFloat(div.style.height)
    if (![left, top, width, height].every(Number.isFinite)) return false

    div.style.left = Math.floor(left + dLeft) + 'px'
    div.style.top = Math.floor(top + dTop) + 'px'
    div.style.width = Math.max(1, Math.floor(width + dWidth)) + 'px'
    div.style.height = Math.max(1, Math.floor(height + dHeight)) + 'px'
    return true
  }

  function processHighlight(div) {
    if (processedHighlights.has(div)) return
    let tries = 0
    const attempt = () => {
      if (!div.isConnected) return
      tries++
      let done = false
      try {
        done = scanAndExpand(div)
      } catch {
        done = false
      }
      if (done) {
        processedHighlights.add(div)
        return
      }
      // 画布可能还没渲染/还没翻到，稍后重试几次
      if (tries < 5) window.setTimeout(attempt, 350)
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
})()
