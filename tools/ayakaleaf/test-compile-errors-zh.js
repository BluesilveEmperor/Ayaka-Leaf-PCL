// 编译错误汉化脚本的离线校验（不依赖 WebView2/浏览器）
// 用法：node tools/ayakaleaf/test-compile-errors-zh.js
const fs = require('fs')
const path = require('path')

global.window = global

const scriptPath = path.join(__dirname, '..', '..', 'Plain Craft Launcher 2', 'Resources', 'ayakaleaf', 'compile-errors-zh.js')

const payload = {
  output: {
    log: [
      {
        level: 'error',
        content: "! LaTeX Error: File `tikz-cd.sty' not found.",
        file: 'main.tex',
        line: 5,
        column: 1,
      },
      { level: 'error', content: '! Missing $ inserted.', file: 'main.tex', line: 7 },
      {
        level: 'error',
        content: '! Package inputenc Error: Unicode character （ (U+FF08) not set up for use with LaTeX.',
        file: 'main.tex',
        line: 9,
      },
      { level: 'error', content: '! Emergency stop.', file: 'main.tex', line: 12 },
      { level: 'warning', content: 'Underfull \\hbox (badness 10000) in paragraph', file: 'main.tex', line: 3 },
      // 小语种缺口（Edge Case：精简镜像未内置小语种语言/字体包）
      { level: 'error', content: "! Package babel Error: Unknown language `ngerman'.", file: 'main.tex', line: 14 },
      {
        level: 'error',
        content: 'fontspec Error: The font "Hiragino Mincho ProN" cannot be found.',
        file: 'main.tex',
        line: 16,
      },
    ],
  },
}

const compileUrl = 'http://localhost:8080/project/0123456789abcdef01234567/compile'
let requestedUrl = null
global.fetch = async (url, init) => {
  requestedUrl = url
  if (String(init && init.method).toUpperCase() !== 'POST') throw new Error('unexpected method')
  return new Response(JSON.stringify(payload), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}

eval(fs.readFileSync(scriptPath, 'utf8'))

;(async () => {
  const resp = await window.fetch(compileUrl, { method: 'POST' })
  const data = await resp.json()
  const log = data.output.log

  const checks = [
    ['missingPackage', log[0], /【中文提示】缺少宏包或文档类「tikz-cd\.sty」/],
    ['mathSyntax', log[1], /【中文提示】公式／命令语法错误/],
    ['encoding', log[2], /【中文提示】文档里有当前编译引擎认不出的字符/],
    ['unknownKept', log[3], /^! Emergency stop\.$/],
    ['warningUntouched', log[4], /^Underfull/],
    ['minorLangBabel', log[5], /缺少语言支持宏包：文档使用的语言「ngerman」/],
    ['minorLangFont', log[6], /缺少字体或语言支持文件「Hiragino Mincho ProN」/],
  ]

  let failed = 0
  for (const [name, entry, re] of checks) {
    const ok = re.test(entry.content)
    console.log(`${ok ? 'PASS' : 'FAIL'} ${name}: ${JSON.stringify(entry.content.slice(0, 90))}`)
    if (!ok) failed++
  }

  // 文件/行号必须保持原样（决定官方跳转是否准）
  const lineKept = log[0].file === 'main.tex' && log[0].line === 5
  console.log(`${lineKept ? 'PASS' : 'FAIL'} jumpMeta: file=${log[0].file} line=${log[0].line}`)
  if (!lineKept) failed++

  // 幂等：再次编译同一条日志不应重复追加
  const again = await (await fetch(compileUrl, { method: 'POST' })).json()
  const duplicated = (again.output.log[0].content.match(/【中文提示】/g) || []).length > 1
  console.log(`${duplicated ? 'FAIL' : 'PASS'} idempotent: 追加次数=${(again.output.log[0].content.match(/【中文提示】/g) || []).length}`)
  if (duplicated) failed++

  console.log(`\n${failed === 0 ? 'ALL PASS' : failed + ' FAILED'}`)
  process.exit(failed === 0 ? 0 : 1)
})()
