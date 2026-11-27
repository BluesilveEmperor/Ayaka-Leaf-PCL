/*
 * Ayakaleaf 工作台 PDF 反向同步（US7：单击 PDF 跳回源码行）
 * ---------------------------------------------------------------------------
 * 官方只有「双击 PDF」反向同步：textLayer 上的 dblclick → clickPosition（pdf.js
 * viewport.convertToPdfPoint 换算坐标）→ window 派发 'synctex:sync-to-position'
 * → React 冒泡监听 → GET /project/:id/sync/pdf → goToCodeLine。
 * 本脚本把「单击」也接入同一链路，并把定位请求交给宿主执行
 * （SyncTexReverseService，经 WebView2 消息通道）：
 *   - 命中当前打开文件：直接派发官方 editor:gotoLine 快速跳行，并闪烁高亮该行；
 *   - 命中其他文件：原样重放官方事件，由官方流程打开目标文件并跳转；
 *   - 未命中（no-hit）：静默（官方流程会弹错误提示，不打扰）；
 *   - 通道异常（error）：原样重放官方事件兜底，行为与官方双击一致。
 *
 * 实现要点（全部经镜像内 bundle 反解验证，2026-11）：
 *  1. 编译上下文捕获：包装 window.fetch，观察编译 POST 响应的
 *     outputFiles[type=pdf]（editorId/build）与顶层 clsiServerId，以及 /sync/code
 *     请求 URL 上的 editorId/buildId/clsiserverid —— web 端 proxySyncPdf
 *     强制校验 editorId/buildId（/^[a-f0-9-]+$/），缺失即 500。
 *  2. 单击识别：PDF 页面（.pdfViewer .page）内的 click，经 260ms 防抖；
 *     防抖期内出现真实 dblclick 则取消，官方双击流程原样保留。
 *     拖拽（>4px）、划选文本、链接/表单元素点击不触发。
 *  3. 坐标换算：向页面的 .textLayer（官方 'textlayerrendered' 钩子挂 dblclick
 *     的同一元素）派发合成 dblclick（真实 clientX/Y），官方 clickPosition 用
 *     canvas rect + convertToPdfPoint 换算成 PDF 坐标（bp）。
 *  4. 事件拦截：window 捕获阶段监听 'synctex:sync-to-position'，仅拦截带
 *     pendingSynthetic 标记的合成事件（本脚本先于 React 注册，捕获阶段先于
 *     官方的冒泡监听触发，stopImmediatePropagation 可完全抑制官方分支）。
 *     事件 detail：{position:{page(0 基),offset:{left,top}},selectText}；
 *     官方 _syncToCode 对 clickPosition 产物（无 pageSize）取 h=left、v=top、
 *     page+1 发给宿主。
 *  5. 宿主回复（chrome.webview message）：
 *     - ayaka:pdf-reverse-locate {file,line,column}：
 *       file 与当前打开文档（editor.open_doc_name，规范化 "/./" 段）一致 →
 *       派发 editor:gotoLine {gotoLine,selectText:''}（官方 use-codemirror-scope
 *       监听该事件并 setCursorLineAndScroll）+ 闪烁该行；
 *       不一致 → 原样重放官方事件（打开目标文件并跳转）。
 *     - ayaka:pdf-reverse-miss {reason:'no-hit'|'error'}：见顶部说明。
 *     宿主 1.5s 无响应同样重放官方事件兜底。
 *
 * 已知局限（与官方一致）：页面文本层（textLayer）未渲染时无法反向定位；
 * 多栏/双面等复杂布局的命中精度取决于 synctex 数据本身。
 *
 * 注入方式：WebView2 的 AddScriptToExecuteOnDocumentCreatedAsync，每次文档创建
 *          都执行，先于应用 bundle 运行，因此可以安全包装 window.fetch。
 */

(() => {
  'use strict'

  if (window.__ayakaPdfReverseSync) return
  window.__ayakaPdfReverseSync = true

  // 注入版本号：宿主把 [ayaka-pdf-reverse] 日志经 postMessage 写进 PCL 日志，
  // 用来确认「跑的是哪一版脚本」
  const BRIDGE_VERSION = '1'

  // 单击防抖：期间发生 dblclick 则取消，官方双击反向同步不受影响
  const CLICK_DEBOUNCE_MS = 260
  // 拖拽阈值（px）：按下-抬起位移超过该值视为拖动，不是单击
  const DRAG_THRESHOLD_PX = 4
  // 同一位置短时间重复点击不重复触发
  const SAME_CLICK_QUIET_MS = 1200
  // 等待宿主回复的上限；超时重放官方事件兜底
  const HOST_REPLY_TIMEOUT_MS = 1500
  // 合成 dblclick 的安全网：官方钩子没接管时及时清掉拦截标记，
  // 避免吞掉用户之后的真实双击
  const SYNTHETIC_GUARD_MS = 300
  // 跳转后闪烁的持续时间（与样式表中的 animation 时长保持一致）
  const FLASH_MS = 1700

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

  report(`[ayaka-pdf-reverse] bridge v${BRIDGE_VERSION} 已注入（${location.pathname}）`)

  /* ------------------------------------------------------------------ *
   * 编译上下文捕获（web 端 proxySyncPdf 强制要求 editorId/buildId）
   * ------------------------------------------------------------------ */

  const compileCtx = { editorId: '', buildId: '', clsiServerId: '' }

  function queryParam(url, name) {
    const m = new RegExp('[?&]' + name + '=([^&]*)').exec(url)
    return m ? decodeURIComponent(m[1]) : ''
  }

  // 正向同步（/sync/code）请求 URL 上带有同一套编译上下文参数
  function noteSyncCodeUrl(url) {
    const editorId = queryParam(url, 'editorId')
    if (editorId) compileCtx.editorId = editorId
    const buildId = queryParam(url, 'buildId')
    if (buildId) compileCtx.buildId = buildId
    const clsiServerId = queryParam(url, 'clsiserverid')
    if (clsiServerId) compileCtx.clsiServerId = clsiServerId
  }

  // 编译 POST 响应：outputFiles[type=pdf] 带 editorId/build，顶层带 clsiServerId
  function noteCompileResponse(resp) {
    if (!resp || !resp.ok) return
    if ((resp.headers.get('content-type') || '').indexOf('json') < 0) return
    resp.clone().json().then(data => {
      if (!data) return
      if (typeof data.clsiServerId === 'string' && data.clsiServerId) {
        compileCtx.clsiServerId = data.clsiServerId
      }
      if (!Array.isArray(data.outputFiles)) return
      const pdf =
        data.outputFiles.find(f => f && f.type === 'pdf') ||
        data.outputFiles.find(f => f && String(f.path || '').slice(-4) === '.pdf')
      if (!pdf) return
      const editorId = pdf.editorId || data.editorId
      if (editorId) compileCtx.editorId = String(editorId)
      const buildId = pdf.build || data.buildId
      if (buildId) compileCtx.buildId = String(buildId)
    }).catch(() => {})
  }

  function requestUrlOf(input) {
    if (typeof input === 'string') return input
    if (input && typeof input.url === 'string') return input.url
    return ''
  }

  const SYNC_CODE_URL_RE = /\/sync\/code\?/
  const COMPILE_URL_RE = /\/project\/[^/?]+\/compile/

  // 包装 window.fetch：只旁路记录编译上下文，请求/响应本身原样透传。
  // 与 editor-synctex.js / compile-errors-zh.js 的包装链叠加安全（各自 clone 读取）。
  const origFetch = window.fetch
  if (typeof origFetch === 'function') {
    window.fetch = function (input, init) {
      const url = requestUrlOf(input)
      const isSyncCode = SYNC_CODE_URL_RE.test(url)
      const isCompile =
        !isSyncCode &&
        COMPILE_URL_RE.test(url) &&
        init &&
        init.method &&
        String(init.method).toUpperCase() === 'POST'

      if (isSyncCode) noteSyncCodeUrl(url)

      const promise = origFetch.call(this, input, init)
      if (isCompile) promise.then(noteCompileResponse).catch(() => {})
      return promise
    }
  }

  /* ------------------------------------------------------------------ *
   * 工具
   * ------------------------------------------------------------------ */

  function isProjectPage() {
    return /^\/project\/[0-9a-zA-Z]{8,}/.test(location.pathname)
  }

  function getStore() {
    try {
      return window.overleaf?.unstable?.store ?? null
    } catch {
      return null
    }
  }

  function getOpenDocName() {
    const store = getStore()
    if (!store || typeof store.get !== 'function') return ''
    try {
      const name = store.get('editor.open_doc_name')
      return typeof name === 'string' ? name : ''
    } catch {
      return ''
    }
  }

  // 规范化 synctex 路径：官方对根文档在子目录时会生成 "dir/./main.tex" 形式
  function normalizeTexPath(p) {
    return String(p).replace(/\/\.\//g, '/').replace(/^\.\//, '')
  }

  /* ------------------------------------------------------------------ *
   * 跳转闪烁样式与实现
   * ------------------------------------------------------------------ */

  function ensureFlashStyle() {
    if (document.getElementById('ayaka-reverse-flash-style')) return
    const style = document.createElement('style')
    style.id = 'ayaka-reverse-flash-style'
    style.textContent =
      '@keyframes ayaka-reverse-flash{0%{background-color:rgba(255,213,79,.55)}' +
      '100%{background-color:transparent}}' +
      '.cm-line.ayaka-reverse-flash{animation:ayaka-reverse-flash 1.6s ease-out 1}'
    if (document.head) document.head.appendChild(style)
  }

  // 跳转完成后闪烁对应行（CodeMirror 视图经 unstable store 获取；失败只影响闪烁）
  function flashLine(line) {
    window.setTimeout(() => {
      try {
        ensureFlashStyle()
        const store = getStore()
        const view = store && typeof store.get === 'function' ? store.get('editor.view') : null
        if (!view || !view.state || !view.state.doc) return
        const total = view.state.doc.lines
        const docLine = view.state.doc.line(Math.min(Math.max(line, 1), total))
        const pos = view.domAtPos(docLine.from)
        const node = pos.node.nodeType === 1 ? pos.node : pos.node.parentElement
        const lineEl = node && node.closest ? node.closest('.cm-line') : null
        if (!lineEl) return
        lineEl.classList.add('ayaka-reverse-flash')
        window.setTimeout(() => lineEl.classList.remove('ayaka-reverse-flash'), FLASH_MS)
      } catch {
        // 闪烁失败不影响跳转本身
      }
    }, 120)
  }

  /* ------------------------------------------------------------------ *
   * 事件拦截、宿主往返与官方流程重放
   * ------------------------------------------------------------------ */

  // 仅拦截「本脚本合成 dblclick」产生的 synctex 事件；用户真实双击不打此标记
  let pendingSynthetic = false
  // 拦截到的原始事件 detail（跨文件 / 通道异常时原样重放，走官方流程）
  let stashedDetail = null
  // 在途请求序号：超时兜底只作用于最新一次点击
  let pendingSeq = 0

  function replayOfficial(detail) {
    // 重放时不带 pendingSynthetic 标记 → 捕获监听放行 → React 官方流程接管
    try {
      window.dispatchEvent(new CustomEvent('synctex:sync-to-position', { detail }))
    } catch (err) {
      report('[ayaka-pdf-reverse] 重放官方同步事件失败：' + (err && err.message ? err.message : err))
    }
  }

  // 捕获阶段拦截（本脚本先于 React 注册；官方 useEventListener 是 window 冒泡监听）
  window.addEventListener(
    'synctex:sync-to-position',
    e => {
      if (!pendingSynthetic) return
      pendingSynthetic = false
      if (!e || !e.detail || !e.detail.position) return
      e.stopImmediatePropagation()
      handleReversePosition(e.detail)
    },
    true
  )

  function handleReversePosition(detail) {
    const position = detail.position
    const page = position.page + 1 // clickPosition 返回 0 基页码，端点要求 1 基
    const x = position.offset.left // bp，距页面左缘
    const y = position.offset.top // bp，距页面顶部（官方 _syncToCode 无 pageSize 分支同款取值）
    if (!Number.isFinite(page) || !Number.isFinite(x) || !Number.isFinite(y)) return

    const webview = window.chrome && window.chrome.webview
    if (!webview || typeof webview.postMessage !== 'function') {
      // 无宿主通道（非工作台环境）：放行官方流程
      replayOfficial(detail)
      return
    }

    stashedDetail = detail
    const seq = ++pendingSeq
    webview.postMessage({
      type: 'ayaka:pdf-reverse-click',
      page,
      x,
      y,
      editorId: compileCtx.editorId,
      buildId: compileCtx.buildId,
      clsiserverid: compileCtx.clsiServerId,
      path: location.pathname
    })
    report(
      `[ayaka-pdf-reverse] 第 ${page} 页 (${x.toFixed(1)}, ${y.toFixed(1)})bp → 宿主定位` +
        `（editorId ${compileCtx.editorId ? '已捕获' : '缺失'}，` +
        `buildId ${compileCtx.buildId ? '已捕获' : '缺失'}）`
    )

    // 超时兜底：宿主无响应（崩溃/卡顿）时重放官方事件
    window.setTimeout(() => {
      if (seq !== pendingSeq || stashedDetail !== detail) return
      stashedDetail = null
      report('[ayaka-pdf-reverse] 宿主回复超时，回退官方流程')
      replayOfficial(detail)
    }, HOST_REPLY_TIMEOUT_MS)
  }

  function consumeStash() {
    const detail = stashedDetail
    stashedDetail = null
    return detail
  }

  function onHostLocate(msg) {
    const detail = consumeStash()
    if (!detail) return // 已被超时兜底处理 / 新一轮点击接管
    const file = String(msg.file || '')
    const line = Number(msg.line) || 0
    if (!file || line < 1) return

    const openDocName = getOpenDocName()
    if (openDocName && normalizeTexPath(openDocName) === normalizeTexPath(file)) {
      // 同文件快速路径：文档已打开，直接派发官方跳行事件（use-codemirror-scope
      // 监听 editor:gotoLine 并 setCursorLineAndScroll），随后闪烁该行
      window.dispatchEvent(
        new CustomEvent('editor:gotoLine', { detail: { gotoLine: line, selectText: '' } })
      )
      flashLine(line)
      report(`[ayaka-pdf-reverse] 命中 ${file}:${line}（同文件快速跳行）`)
    } else {
      // 跨文件：重放官方事件（打开目标文件并跳转）；宿主已取过一次结果，
      // 官方流程会再取一次，多一次本地查询可接受
      report(`[ayaka-pdf-reverse] 命中 ${file}:${line}（跨文件，转官方流程）`)
      replayOfficial(detail)
    }
  }

  function onHostMiss(msg) {
    const detail = consumeStash()
    if (!detail) return
    if (msg.reason === 'error') {
      // 访客模式 / 会话缺失 / 定位通道异常：回退官方反向同步流程
      report('[ayaka-pdf-reverse] 定位通道异常，回退官方流程')
      replayOfficial(detail)
    } else {
      // 'no-hit'：点击处无对应源码记录，静默（官方流程会弹错误提示）
      report('[ayaka-pdf-reverse] 未命中（该位置无 SyncTeX 记录）')
    }
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', e => {
      const msg = e && e.data
      if (!msg || typeof msg.type !== 'string') return
      if (msg.type === 'ayaka:pdf-reverse-locate') {
        onHostLocate(msg)
      } else if (msg.type === 'ayaka:pdf-reverse-miss') {
        onHostMiss(msg)
      }
    })
  }

  /* ------------------------------------------------------------------ *
   * 单击捕获 → 合成 dblclick
   * ------------------------------------------------------------------ */

  let downX = 0
  let downY = 0
  let clickTimer = null
  let lastClickKey = ''
  let lastClickAt = 0

  document.addEventListener(
    'mousedown',
    e => {
      downX = e.clientX
      downY = e.clientY
    },
    true
  )

  document.addEventListener(
    'dblclick',
    () => {
      // 真实双击：取消待定的单击反向同步，官方双击流程原样保留
      if (clickTimer) {
        window.clearTimeout(clickTimer)
        clickTimer = null
      }
    },
    true
  )

  document.addEventListener(
    'click',
    e => {
      if (!isProjectPage()) return
      const target = e.target
      if (!target || typeof target.closest !== 'function') return

      const pageEl = target.closest('.pdfViewer .page')
      if (!pageEl) return
      // 链接/表单等交互元素内的点击不触发
      if (target.closest('a, button, input, textarea, select')) return
      // 按下-抬起位移超阈值：拖动而非单击
      if (
        Math.abs(e.clientX - downX) > DRAG_THRESHOLD_PX ||
        Math.abs(e.clientY - downY) > DRAG_THRESHOLD_PX
      )
        return
      // 划选了文本：用户在复制 PDF 内容，不是反向同步
      const selection = window.getSelection()
      if (selection && !selection.isCollapsed && selection.toString()) return
      // 文本层未渲染：官方 dblclick 同样无效，直接放弃
      const textLayer = pageEl.querySelector('.textLayer')
      if (!textLayer) return

      const key =
        (pageEl.getAttribute('data-page-number') || '0') +
        ':' + Math.round(e.clientX) + ',' + Math.round(e.clientY)
      const now = Date.now()
      if (key === lastClickKey && now - lastClickAt < SAME_CLICK_QUIET_MS) return

      if (clickTimer) window.clearTimeout(clickTimer)
      clickTimer = window.setTimeout(() => {
        clickTimer = null
        lastClickKey = key
        lastClickAt = Date.now()
        fireSyntheticDblclick(textLayer, e.clientX, e.clientY)
      }, CLICK_DEBOUNCE_MS)
    },
    true
  )

  // 合成 dblclick：派发到官方 'textlayerrendered' 钩子挂监听的同一元素上，
  // 复用官方 clickPosition 的坐标换算（canvas rect + convertToPdfPoint）
  function fireSyntheticDblclick(textLayer, clientX, clientY) {
    pendingSynthetic = true
    // 安全网：官方钩子未接管（文本层重渲染/结构变化）时清掉标记，
    // 避免吞掉用户后续真实双击
    window.setTimeout(() => {
      pendingSynthetic = false
    }, SYNTHETIC_GUARD_MS)
    const ev = new MouseEvent('dblclick', {
      bubbles: false,
      cancelable: true,
      clientX,
      clientY,
      view: window
    })
    textLayer.dispatchEvent(ev)
  }

  // 调试用：控制台手动触发一次单击链路（传入 .page 内任一元素与坐标）
  window.__ayakaPdfReverseTest = (el, clientX, clientY) => {
    const pageEl = el && el.closest ? el.closest('.pdfViewer .page') : null
    const textLayer = pageEl && pageEl.querySelector('.textLayer')
    if (!textLayer) {
      report('[ayaka-pdf-reverse] 测试失败：未找到 textLayer')
      return
    }
    fireSyntheticDblclick(textLayer, clientX || 100, clientY || 100)
  }
})()
