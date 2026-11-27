/*
 * Ayakaleaf 工作台编译错误汉化（US8 / FR-013 / FR-014）
 * ---------------------------------------------------------------------------
 * 目标：编译失败时，在错误条目里补一行简体中文解释，覆盖三类初学者高频错误
 *      （编码、宏包缺失、公式/命令语法）；未命中的错误保持原样，绝不翻译或清空。
 *
 * 实现方式：拦截 POST /project/:id/compile 的 JSON 响应，逐条改写 output.log[].content。
 *   - 只改文本，不动 file / line / column，因此 Overleaf 原生「点错误跳转到源码行」
 *     的交互完全保留——中文标注就长在那条错误里，点它同样会跳转。
 *   - 规则未命中（或响应结构变化）时原样返回，不吞异常、不改写。
 *   - 已在脚本里打过标记（CONTENT_MARK）的条目不重复追加，避免多次编译叠加。
 *
 * 与 plan.md C2 契约的差异：C2 设想宿主侧命中规则后通过 postMessage 回推。
 * 实测在页面侧改写响应更稳（无往返延迟、不受 WebView2 消息时序影响），
 * 且跳转行为直接复用官方实现；规则仍集中在本文档，便于后续版本化更新。
 *
 * 注入方式：AddScriptToExecuteOnDocumentCreatedAsync —— 先于应用 bundle 执行，
 *          因此可以安全包装 window.fetch（与 editor-synctex.js 同样的处理）。
 */

(() => {
  'use strict'

  if (window.__ayakaCompileErrorsZh) return
  window.__ayakaCompileErrorsZh = true

  /** 追加的中文行前缀，同时用作幂等标记 */
  const CONTENT_MARK = '【中文提示】'

  const COMPILE_URL_RE = /\/project\/[0-9a-f]{24}\/compile(?:\?|$)/
  /** 最常见的几类：定义顺序在前的规则优先 */
  const RULES = [
    {
      id: 'encoding',
      test: [
        /Unicode character .{0,40}not set up for use with LaTeX/i,
        /inputenc Error: Unicode char/i,
        /Package inputenc Error/i,
        /Invalid UTF-8 byte/i,
        /Unicode char .{0,40}\(U\+[0-9A-F]+\)/i,
      ],
      hint: (m) =>
        '文档里有当前编译引擎认不出的字符（通常是中文标点、特殊空格或生僻符号）。' +
        '处理办法：把编译器切到 XeLaTeX / LuaLaTeX，并在导言区用 \\usepackage[UTF8]{ctex}；' +
        '若确认是某个符号，直接替换或删掉它即可。',
    },
    {
      id: 'missingPackage',
      test: [
        /File `([^`']+)' not found/i,
        /! LaTeX Error: File `([^`']+)' not found/i,
        /Package ([A-Za-z0-9@_-]+) Error: .{0,80}not (?:installed|found)/i,
        /You have requested (?:package|document class) `?([A-Za-z0-9@_-]+)/i,
        // 小语种缺口（Edge Case：精简镜像未内置小语种语言/字体包）
        /! Package babel Error: Unknown (?:option|language) [`"']?([A-Za-z-]+)/i,
        /! Font [^=]{0,60}=([^\s]+) not loadable: Metric \(TFM\) file not found/i,
        /fontspec[^:]{0,80}: The font [`"']([^`"']+)[`"'] cannot be found/i,
      ],
      hint: (m) => {
        const raw = m[0] || ''
        const file = m[1] || ''
        const name = file.replace(/\.(sty|cls|def|cfg|ldf|tfm)$/i, '')
        if (/babel Error/i.test(raw))
          return `缺少语言支持宏包：文档使用的语言「${name}」在当前环境不可用（精简镜像未内置小语种语言包）。` +
            `可在启动器「服务台 → 宏包管理」搜索对应语言包（如 babel 系语言包）安装后重新编译；` +
            '或到「镜像管理」页改用完整版镜像。'
        if (/not loadable|cannot be found/i.test(raw))
          return `缺少字体或语言支持文件「${name}」（精简镜像未内置小语种字体/字体包）。` +
            '可在启动器「服务台 → 宏包管理」搜索对应宏包安装后重新编译；' +
            '或到「镜像管理」页改用完整版镜像。'
        return name
          ? `缺少宏包或文档类「${file || name}」：镜像里没有这个文件。` +
            `可在启动器「服务台 → 宏包管理」搜索 ${name} 安装后重新编译（精简镜像用户尤其常见）。`
          : '缺少宏包或文档类文件。可在启动器「服务台 → 宏包管理」搜索安装后重新编译。'
      },
    },
    {
      id: 'mathSyntax',
      test: [
        /Missing \$ inserted/i,
        /Missing \{ inserted/i,
        /Missing \} inserted/i,
        /Undefined control sequence/i,
        /Double subscript/i,
        /Double superscript/i,
        /Misplaced alignment tab character/i,
        /Extra \}, or forgotten/i,
        /Package amsmath Error/i,
        /Runaway argument/i,
        /There's no line here to end/i,
      ],
      hint: (m) => {
        const raw = m[0] || ''
        let detail = ''
        if (/Undefined control sequence/i.test(raw))
          detail = '命令名拼错了，或它所属的宏包没有 \\usepackage 进来'
        else if (/Missing \$ inserted/i.test(raw))
          detail = '数学符号写在了公式环境外面，需要补 $ 或放进 equation 等环境'
        else if (/Missing \{|\}/i.test(raw))
          detail = '花括号不配对，常见于 \\frac、\\textbf 等命令的参数'
        else if (/alignment tab character/i.test(raw))
          detail = '& 只能用在表格/对齐环境里，正文中请写 \\&'
        else if (/Double (subscript|superscript)/i.test(raw))
          detail = '同一个符号上出现了两个下标或上标，需要用花括号包起来'
        if (detail) return `公式／命令语法错误：${detail}。请对照本行源码检查。`
        return '公式或命令语法有误（漏 $、花括号不配对、环境未闭合等）。请对照本行源码检查。'
      },
    },
  ]

  /**
   * 为一条日志文本生成中文提示；未命中返回 null（FR-014：保持原样）。
   */
  function hintFor(text) {
    if (!text || text.indexOf(CONTENT_MARK) >= 0) return null
    for (const rule of RULES) {
      for (const re of rule.test) {
        const m = text.match(re)
        if (m) {
          try {
            return rule.hint(m)
          } catch (err) {
            return null
          }
        }
      }
    }
    return null
  }

  /** 给单条日志补中文行；返回是否改动 */
  function annotateEntry(entry) {
    if (!entry || typeof entry !== 'object') return false
    // 只处理错误：warning 也汉化会淹没真正的失败原因
    if (entry.level && String(entry.level).toLowerCase() !== 'error') return false

    const content = typeof entry.content === 'string' ? entry.content : ''
    const message = typeof entry.message === 'string' ? entry.message : ''
    const hint = hintFor(content) || hintFor(message)
    if (!hint) return false

    entry.content = (content ? content.replace(/\s+$/, '') + '\n' : '') + CONTENT_MARK + hint
    return true
  }

  /** 改写 compile 响应体；结构不符时返回 null 表示"不要替换响应" */
  function localizePayload(data) {
    const log = data && data.output && Array.isArray(data.output.log) ? data.output.log : null
    if (!log) return null

    let changed = 0
    for (const entry of log) {
      if (annotateEntry(entry)) changed++
    }
    if (changed === 0) return null
    return JSON.stringify(data)
  }

  const origFetch = window.fetch
  if (typeof origFetch !== 'function') return

  window.fetch = function (input, init) {
    const url =
      typeof input === 'string' ? input : input && typeof input.url === 'string' ? input.url : ''
    const isCompile =
      COMPILE_URL_RE.test(url) &&
      init &&
      init.method &&
      String(init.method).toUpperCase() === 'POST'

    const promise = origFetch.call(this, input, init)
    if (!isCompile) return promise

    return promise.then(resp => {
      if (!resp || !resp.ok) return resp
      const type = resp.headers.get('content-type') || ''
      if (type.indexOf('json') < 0) return resp

      return resp
        .clone()
        .json()
        .then(data => {
          const body = localizePayload(data)
          if (!body) return resp
          const headers = new Headers(resp.headers)
          headers.delete('content-length')
          headers.delete('content-encoding')
          return new Response(body, {
            status: resp.status,
            statusText: resp.statusText,
            headers,
          })
        })
        .catch(() => resp) // 解析失败一律回退原响应，绝不影响编译结果展示
    })
  }
})()
