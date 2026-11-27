/*
 * Ayakaleaf 工作台文档导入失败汉化与超大文档防护（US1 / FR-003）
 * ---------------------------------------------------------------------------
 * 目标：
 *   1. Word / Markdown / Zip 文档导入失败时，给出明确的简体中文原因提示
 *      （网络中断、超过大小限制、格式不支持、转换超时、服务端缺 Pandoc 等），
 *      不再只有 Overleaf 原生的英文报错一闪而过甚至静默失败。
 *   2. 超大文档防护（FR-003 Edge Case）：转换类导入（.docx / .md）在浏览器侧
 *      预检体积 —— 超过 100MB 直接拦截并提示拆分；30MB~100MB 提示“转换较慢”；
 *      上传过程加 5 分钟兜底超时，避免 pandoc 卡死时前端无限等待。
 *
 * 实现方式：两条通道叠加，互为兜底：
 *   - 包装 window.fetch：拦截 POST /project/new/upload（新建项目导入）与
 *     POST /project/:id/upload（项目内上传）；
 *   - capture 阶段监听 form submit：覆盖旧版页面走原生表单提交的导入入口
 *     （此时至少保留超大文档拦截，失败原因由 Overleaf 自行展示）。
 *
 * 约束：
 *   - 只做“提示叠加”，不改写请求与响应体，Overleaf 原生错误处理流程完全保留；
 *   - 提示使用页面内自绘 toast（固定定位、亮暗主题均可读），并通过
 *     postMessage（ayaka:importLog）把结果上报宿主日志；
 *   - 非 WebView2 环境没有 chrome.webview 时静默跳过上报。
 *
 * 注入方式：AddScriptToExecuteOnDocumentCreatedAsync —— 先于应用 bundle 执行
 *          （与 compile-errors-zh.js 同样的处理）。
 */

(() => {
  'use strict'

  if (window.__ayakaImportErrorsZh) return
  window.__ayakaImportErrorsZh = true

  /** 转换类导入的体积阈值（FR-003 Edge Case：超大文档控制） */
  const WARN_BYTES = 30 * 1024 * 1024 // 30MB：提示转换可能较慢
  const MAX_BYTES = 100 * 1024 * 1024 // 100MB：直接拦截，提示拆分
  /** pandoc 转换兜底超时：超时后中止请求并提示，避免前端无限等待 */
  const CONVERT_TIMEOUT_MS = 5 * 60 * 1000

  /** 新建项目导入与项目内上传两类端点 */
  const UPLOAD_URL_RE = /\/project\/(?:new|[0-9a-f]{24})\/upload(?:\?|$)/
  const FORM_ACTION_RE = /\/project\/(?:new|[0-9a-f]{24})\/upload/

  /** 供宿主日志排查：ayaka:importLog → WorkbenchWindow.Web_WebMessageReceived */
  function logToHost(message) {
    try {
      const webview = window.chrome && window.chrome.webview
      if (webview && typeof webview.postMessage === 'function')
        webview.postMessage({ type: 'ayaka:importLog', message })
    } catch (err) {
      /* 上报失败不影响页面行为 */
    }
  }

  let toastTimer = null

  /** 页面内自绘 toast：亮暗主题均可读；error 红色常驻 10s，提示蓝色 5s */
  function showToast(text, kind) {
    try {
      let host = document.getElementById('ayaka-import-toast')
      if (!host) {
        host = document.createElement('div')
        host.id = 'ayaka-import-toast'
        host.style.cssText = [
          'position:fixed', 'z-index:2147483647', 'left:50%', 'bottom:32px',
          'transform:translateX(-50%)', 'max-width:560px', 'padding:12px 18px',
          'border-radius:8px', 'font-size:14px', 'line-height:1.6',
          'box-shadow:0 4px 16px rgba(0,0,0,.35)', 'color:#fff',
          'white-space:pre-line', 'display:none',
        ].join(';')
        if (document.body) document.body.appendChild(host)
      }
      host.style.background = kind === 'error' ? '#a93226' : '#1f5f8b'
      host.textContent = text
      host.style.display = 'block'
      if (toastTimer) clearTimeout(toastTimer)
      toastTimer = setTimeout(() => {
        host.style.display = 'none'
      }, kind === 'error' ? 10000 : 5000)
    } catch (err) {
      /* DOM 尚未就绪等异常时放弃 toast，仅保留宿主日志 */
    }
    logToHost(text)
  }

  function fileNameOf(file) {
    return file && typeof file.name === 'string' && file.name ? `「${file.name}」` : '文档'
  }

  /** 从 FormData 提取第一个文件条目（file / files[]） */
  function firstFileOf(formData) {
    try {
      for (const entry of formData.entries()) {
        const key = entry[0]
        const value = entry[1]
        if ((key === 'file' || key === 'files[]') &&
            value && typeof value === 'object' && typeof value.name === 'string')
          return value
      }
    } catch (err) {
      /* entries 不可枚举时按无文件处理 */
    }
    return null
  }

  /** 从 FormData 读取 importType（docx / md / upload / zip…） */
  function importTypeOf(formData) {
    try {
      for (const entry of formData.entries()) {
        if (entry[0] === 'importType' && typeof entry[1] === 'string') return entry[1]
      }
    } catch (err) {
      /* 忽略 */
    }
    return ''
  }

  /** 是否为需要 pandoc 转换的导入（Word / Markdown） */
  function isConversion(importType, file) {
    if (importType === 'docx' || importType === 'md') return true
    // 表单提交通道没有 importType 字段时，按扩展名兜底判断
    if (file && typeof file.name === 'string')
      return /\.(docx|md|markdown)$/i.test(file.name)
    return false
  }

  /** 按响应状态码生成中文失败原因（FR-003：导入失败展示明确中文原因） */
  function describeFailure(status, file) {
    const name = fileNameOf(file)
    if (status === 0) return `网络中断或服务不可达：${name}未能上传，请检查服务状态后重试`
    if (status === 413) return `${name}超过服务器大小限制（100MB），请压缩图片、删除多余内容或拆分文档后重试`
    if (status === 415) return `${name}格式不支持：请确认选择的是有效的 .docx / .md 文件（旧版 .doc 需先在 Word 中另存为 .docx）`
    if (status === 504) return `转换超时：${name}过大或过于复杂，建议拆分文档后分批导入`
    if (status === 502 || status === 503) return `服务暂时不可用：${name}导入失败，请稍后重试；若反复出现，请重启 Ayakaleaf 服务`
    if (status >= 500) return `服务器内部错误：${name}导入失败；若为 Word/Markdown 转换，可能是镜像未内置 Pandoc，请更新运行时镜像包后重启服务`
    if (status === 400) return `${name}导入失败：文件可能已损坏或格式不符合要求，请重新导出后再试`
    return `${name}导入失败（HTTP ${status}），请重试；若反复出现，请查看服务日志`
  }

  /** 转换超时的中文提示 */
  function describeTimeout(file) {
    return `转换超时：${fileNameOf(file)}转换超过 5 分钟仍未完成，已中止。` +
      '建议拆分文档、压缩图片后重试；小文档出现此提示请重启 Ayakaleaf 服务。'
  }

  /** 超大文档预检：返回 false 表示已拦截（fetch 通道） */
  function precheckSize(file) {
    if (!file || typeof file.size !== 'number') return true
    if (file.size > MAX_BYTES) {
      showToast(`${fileNameOf(file)}超过 100MB 上限，已取消导入。\n` +
        '请拆分文档、压缩图片（Word 里“压缩图片”）或删减内容后重试。', 'error')
      return false
    }
    if (file.size > WARN_BYTES) {
      showToast(`${fileNameOf(file)}体积较大（${(file.size / 1048576).toFixed(0)}MB），` +
        '上传与转换可能需要较长时间，请耐心等待，期间不要关闭窗口。', 'warn')
    }
    return true
  }

  // ── 通道一：原生表单提交（旧版导入入口）── capture 阶段拦截超大文档 ──
  document.addEventListener('submit', (event) => {
    try {
      const form = event.target
      if (!form || form.tagName !== 'FORM') return
      const action = form.getAttribute('action') || ''
      if (!FORM_ACTION_RE.test(action)) return

      const inputs = form.querySelectorAll('input[type=file]')
      for (const input of inputs) {
        const files = input.files
        if (!files) continue
        for (const file of files) {
          if (isConversion('', file) && !precheckSize(file)) {
            event.preventDefault()
            event.stopPropagation()
            return
          }
        }
      }
    } catch (err) {
      /* 拦截失败时放行原生提交，不影响功能 */
    }
  }, true)

  // ── 通道二：fetch（新版导入入口）── 失败分类 + 体积预检 + 转换超时 ──
  const origFetch = window.fetch
  if (typeof origFetch !== 'function') return

  window.fetch = function (input, init) {
    const url =
      typeof input === 'string' ? input : input && typeof input.url === 'string' ? input.url : ''
    const body = init && init.body
    const isUpload =
      UPLOAD_URL_RE.test(url) &&
      init &&
      String(init.method || '').toUpperCase() === 'POST' &&
      typeof FormData !== 'undefined' &&
      body instanceof FormData
    if (!isUpload) return origFetch.call(this, input, init)

    const file = firstFileOf(body)
    const importType = importTypeOf(body)
    const conversion = isConversion(importType, file)

    // 超大文档预检（FR-003 Edge Case）：转换类导入超限时直接拦截，不发请求
    if (conversion && !precheckSize(file))
      return Promise.reject(new DOMException('文件超过 100MB 上限，已取消导入', 'AbortError'))

    // 转换类导入加兜底超时，避免 pandoc 卡死时前端无限等待
    let timeoutId = null
    if (conversion && !(init.signal)) {
      const controller = new AbortController()
      timeoutId = setTimeout(() => {
        controller.abort(new DOMException('转换超时', 'TimeoutError'))
      }, CONVERT_TIMEOUT_MS)
      init = Object.assign({}, init, { signal: controller.signal })
    }

    return origFetch.call(this, input, init).then(
      (resp) => {
        if (timeoutId) clearTimeout(timeoutId)
        if (resp && !resp.ok) {
          showToast(describeFailure(resp.status, file), 'error')
        }
        return resp
      },
      (err) => {
        if (timeoutId) clearTimeout(timeoutId)
        const name = (err && err.name) || ''
        if (name === 'TimeoutError') {
          showToast(describeTimeout(file), 'error')
        } else if (name !== 'AbortError') {
          // 网络层错误（连接被重置 / DNS / 证书等），fetch 不会给到状态码
          showToast(`网络错误：${fileNameOf(file)}上传中断（${(err && err.message) || '网络异常'}），请检查服务状态后重试`, 'error')
        }
        throw err
      }
    )
  }
})()
