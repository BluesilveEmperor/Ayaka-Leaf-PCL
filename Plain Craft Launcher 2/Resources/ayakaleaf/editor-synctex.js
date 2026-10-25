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
 * 注入方式：WebView2 的 AddScriptToExecuteOnDocumentCreatedAsync，每次文档创建都执行。
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
})()
