// Batch 3 单测：synctex.gz 解析 + 行内公式收窄
// 数据来源：test-assets/synctex-highlight/output.synctex.txt（真实编译产物）
// 运行：node synctex-narrow.test.js（工作目录任意，路径相对本文件解析）
'use strict'
const fs = require('fs')
const path = require('path')

// ---- 浏览器环境桩（沿用 disambiguate.test.js 方案）----
// fetch 桩：任何请求都返回真实 gz 夹具，让解压→解析→收窄全链路可测
const GZ = fs.readFileSync(path.resolve(__dirname, 'output.synctex.gz'))
global.window = {
  fetch: async () => new Response(GZ)
}
global.document = {
  documentElement: {},
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {}
}
global.MutationObserver = class {
  observe() {}
  disconnect() {}
}

// ---- 加载被测脚本 ----
const src = fs.readFileSync(
  path.resolve(__dirname, '../../Plain Craft Launcher 2/Resources/ayakaleaf/editor-synctex.js'),
  'utf8'
)
eval(src)

const parse = global.window.__ayakaParseSynctex
const note = global.window.__ayakaNoteCompile
const narrow = global.window.__ayakaNarrow
if (typeof parse !== 'function') throw new Error('__ayakaParseSynctex 未导出')
if (typeof note !== 'function') throw new Error('__ayakaNoteCompile 未导出')
if (typeof narrow !== 'function') throw new Error('__ayakaNarrow 未导出')

// 独立换算（与实现常数表达式不同序，防止常数写错互相对不上）
const spToBp = (sp) => (sp / 65536) * (7200 / 7227)
const near = (a, b, tol) => Math.abs(a - b) <= (tol || 1e-6)

const TXT = fs.readFileSync(path.resolve(__dirname, 'output.synctex.txt'), 'utf8')
const PROJ = '6af2bf866c48220053e4f900'
const BUILD = 'TESTBUILD1'
const syncUrl = (line, build) =>
  `/project/${PROJ}/sync/code?file=synctex-test.tex&line=${line}&column=1` +
  `&buildId=${build || BUILD}&editorId=00000000-0000-0000-0000-000000000000`

let failed = 0
function report(name, pass, detail) {
  if (!pass) failed++
  console.log((pass ? 'PASS' : 'FAIL') + '  ' + name + (pass ? '' : '  ' + (detail || '')))
}

async function main() {
  /* ---------------- Part C-1：synctex 文本解析 ---------------- */
  const parsed = parse(TXT)

  report('解析：fileTag 建立basename→tag 映射（synctex-test.tex→1）',
    parsed.fileTag.get('synctex-test.tex') === 1 &&
    parsed.fileTag.get('article.cls') === 2)

  const r24 = (parsed.regions.get('1:24') || [])[0]
  report('解析：L24 math 区域 [193.19, 209.56]bp，基线 218.45bp，第 1 页',
    !!r24 &&
    near(r24.x0 * (7200 / (7227 * 65536)), spToBp(12710600)) &&
    near(r24.x1 * (7200 / (7227 * 65536)), spToBp(13784946)) &&
    near(r24.y * (7200 / (7227 * 65536)), spToBp(14370078)) &&
    r24.page === 1,
    JSON.stringify(r24))

  const r29 = (parsed.regions.get('1:29') || [])[0]
  report('解析：L29 math 区域 [380.42, 405.18]bp，第 1 页',
    !!r29 &&
    near(r29.x0 * (7200 / (7227 * 65536)), spToBp(25024318)) &&
    near(r29.x1 * (7200 / (7227 * 65536)), spToBp(26653438)) &&
    r29.page === 1,
    JSON.stringify(r29))

  report('解析：L70 pmatrix 单元格区域恰好 4 个（空对被宽度阈值丢弃），第 2 页',
    (parsed.regions.get('1:70') || []).length === 4 &&
    parsed.regions.get('1:70').every(r => r.page === 2))

  report('解析：L56 同行两个公式 → 2 个区域',
    (parsed.regions.get('1:56') || []).length === 2)

  report('解析：L77 嵌套/相邻配对 → 6 个区域',
    (parsed.regions.get('1:77') || []).length === 6)

  report('解析：align 环境（L73-76）无 math 区域',
    !parsed.regions.has('1:73') && !parsed.regions.has('1:74') &&
    !parsed.regions.has('1:75') && !parsed.regions.has('1:76'))

  report('解析：L46 跨行负宽度配对被丢弃',
    !parsed.regions.has('1:46'))

  /* ---------------- Part C-2：compile 拦截 + 端到端收窄 ---------------- */

  // 模拟一次 compile 响应：outputFiles 提供 synctex.gz 下载 URL
  const compileBody = {
    status: 'success',
    outputFiles: [
      {
        path: 'output.pdf',
        url: `/project/${PROJ}/user/6af1b39a3d3e34a94da2f6ec/build/${BUILD}/output/output.pdf`,
        build: BUILD
      },
      {
        path: 'output.synctex.gz',
        url: `/project/${PROJ}/user/6af1b39a3d3e34a94da2f6ec/build/${BUILD}/output/output.synctex.gz`,
        build: BUILD
      }
    ]
  }
  await note(new Response(JSON.stringify(compileBody), {
    headers: { 'content-type': 'application/json' }
  }))
  // 等后台预取（fetch 桩 → 解压 → 解析）完成
  await new Promise(r => setTimeout(r, 500))

  const setFlag = (line) => {
    global.window.__ayakaFormulaSyncPending = { line, at: Date.now() }
  }
  const flagAlive = () => !!global.window.__ayakaFormulaSyncPending

  // 消歧后的真实 /sync/code 响应数据
  const rowsL29 = [
    { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
    { page: 1, h: 310.605225, v: 196.477631, width: 166.874222, height: 8.855677 }
  ]
  const rowsL24 = [
    { page: 1, h: 133.768356, v: 232.34314, width: 166.874222, height: 8.855677 },
    { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 }
  ]
  const rowsL26 = [
    { page: 1, h: 310.6, v: 137.0, width: 166.87, height: 9 },
    { page: 1, h: 133.77, v: 256.0, width: 166.87, height: 9 },
    { page: 1, h: 310.6, v: 149.0, width: 166.87, height: 9 }
  ]
  const keptL70 = [
    { page: 2, h: 353.610565, v: 176.919113, width: 80.86351, height: 29.203064 },
    { page: 2, h: 364.995636, v: 173.632797, width: 29.887949, height: 11.955168 },
    { page: 2, h: 362.643311, v: 173.632919, width: 34.592598, height: 23.910564 },
    { page: 2, h: 364.995636, v: 161.677628, width: 29.88794, height: 11.955168 }
  ]
  const degenerateL74 = [
    { page: 2, h: 310.605225, v: 183.89296, width: 166.874222, height: 0 }
  ]

  // 1) L29：行盒收窄到公式，无区域的第二行盒丢弃，标记被消费
  setFlag(29)
  let out = await narrow(rowsL29, syncUrl(29))
  const expH = spToBp(25024318)
  const expW = spToBp(26653438 - 25024318)
  report('收窄：L29 两个行盒 → 1 个公式矩形（h≈380.42 w≈24.76 v/height 沿用首行盒）',
    out.length === 1 &&
    near(out[0].h, expH, 0.01) && near(out[0].width, expW, 0.01) &&
    out[0].v === 186.020813 && out[0].height === 11.852388 && out[0].page === 1,
    JSON.stringify(out))
  report('收窄：公式标记一次性消费',
    !flagAlive())

  // 2) L24：收窄到 [193.19, 209.56]bp，用第二行盒（基线所在）的 v/height
  setFlag(24)
  out = await narrow(rowsL24, syncUrl(24))
  report('收窄：L24 收窄到公式矩形，v/height 沿用基线所在行盒',
    out.length === 1 &&
    near(out[0].h, spToBp(12710600), 0.01) &&
    near(out[0].width, spToBp(13784946 - 12710600), 0.01) &&
    out[0].v === 222.716553 && out[0].height === 13.031301,
    JSON.stringify(out))

  // 3) 工具栏/光标同步（无标记）：原样返回
  out = await narrow(rowsL29, syncUrl(29))
  report('守卫：无公式标记（工具栏同步）→ 原样返回', out === rowsL29)

  // 4) 标记行号不匹配：原样返回且标记保留
  setFlag(50)
  out = await narrow(rowsL29, syncUrl(29))
  report('守卫：标记行号不匹配 → 原样返回、标记保留', out === rowsL29 && flagAlive())

  // 5) 过期标记：原样返回且标记清除
  global.window.__ayakaFormulaSyncPending = { line: 29, at: Date.now() - 10000 }
  out = await narrow(rowsL29, syncUrl(29))
  report('守卫：过期标记（>TTL）→ 原样返回、标记清除', out === rowsL29 && !flagAlive())

  // 6) L70：响应里已有更细矩形 → 不收窄
  setFlag(70)
  out = await narrow(keptL70, syncUrl(70))
  report('守卫：L70 已有更细矩形（pmatrix 单元格）→ 不收窄', out === keptL70)

  // 7) L26：无 math 区域 → 不收窄
  setFlag(26)
  out = await narrow(rowsL26, syncUrl(26))
  report('守卫：L26 跨栏行无 math 区域 → 不收窄', out === rowsL26)

  // 8) L74：align 无 math 区域 → 不收窄（退化矩形交给墨迹扫描）
  setFlag(74)
  out = await narrow(degenerateL74, syncUrl(74))
  report('守卫：L74 align 无 math 区域 → 不收窄', out === degenerateL74)

  // 9) 未知 buildId：本页会话没见过该编译 → 不收窄
  setFlag(29)
  out = await narrow(rowsL29, syncUrl(29, 'UNKNOWN_BUILD'))
  report('守卫：未知 buildId → 不收窄', out === rowsL29)

  process.exit(failed ? 1 : 0)
}

main().catch(err => {
  console.error(err)
  process.exit(1)
})
