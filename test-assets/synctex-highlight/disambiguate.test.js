// 消歧逻辑单测：用 Step 0 实测的 /sync/code 响应验证每个分支
// 运行：node disambiguate.test.js（工作目录任意，路径相对本文件解析）
'use strict'
const fs = require('fs')
const path = require('path')

// ---- 浏览器环境桩 ----
global.window = {}
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

const dis = global.window.__ayakaDisambiguate
if (typeof dis !== 'function') throw new Error('__ayakaDisambiguate 未导出')

// ---- 实测响应（sync-resp.jsonl + straddle/col 测试数据） ----
const cases = [
  {
    name: 'L24 行内公式(multicols 左栏)：去重后保留 2 个行盒',
    input: [
      { page: 1, h: 133.768356, v: 232.34314, width: 166.874222, height: 8.855677 },
      { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 },
      { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 },
      { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 },
      { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 },
      { page: 1, h: 133.768356, v: 222.716553, width: 166.874222, height: 13.031301 }
    ],
    expectCount: 2
  },
  {
    name: 'L29 行内公式(multicols 右栏)：去重后保留 2 个行盒',
    input: [
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
      { page: 1, h: 310.605225, v: 196.477631, width: 166.874222, height: 8.855677 },
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 },
      { page: 1, h: 310.605225, v: 186.020813, width: 166.874222, height: 11.852388 }
    ],
    expectCount: 2
  },
  {
    name: 'L43 行内公式(twocolumn)：2 行盒原样保留',
    input: [
      { page: 2, h: 133.768356, v: 232.34314, width: 166.874222, height: 8.855677 },
      { page: 2, h: 133.768356, v: 220.38797, width: 166.874222, height: 8.855677 }
    ],
    expectCount: 2
  },
  {
    name: 'L70 display pmatrix：退化矩形丢弃，公式盒与矩阵元素保留',
    input: [
      { page: 2, h: 353.610565, v: 176.919113, width: 80.86351, height: 29.203064 },
      { page: 2, h: 310.605225, v: 146.719788, width: 166.874222, height: 0 },
      { page: 2, h: 364.995636, v: 173.632797, width: 29.887949, height: 11.955168 },
      { page: 2, h: 362.643311, v: 173.632919, width: 34.592598, height: 23.910564 },
      { page: 2, h: 364.995636, v: 161.677628, width: 29.88794, height: 11.955168 }
    ],
    expectCount: 4,
    expectContains: (r) => r.width > 80 && r.width < 81 // 80.9 公式盒必须保留
  },
  {
    name: 'L74 align 首行：只剩 height=0 退化矩形 → 回退保留（滚动行为不丢）',
    input: [{ page: 2, h: 310.605225, v: 183.89296, width: 166.874222, height: 0 }],
    expectCount: 1
  },
  {
    name: 'L75 align 第二行：整栏宽行盒噪音丢弃，仅留内容级小盒',
    input: [
      { page: 2, h: 378.7, v: 206.6, width: 34, height: 7.7 },
      { page: 2, h: 310.605225, v: 209.4, width: 166.874222, height: 12 },
      { page: 2, h: 378.7, v: 236.5, width: 34.6, height: 7.7 },
      { page: 2, h: 310.605225, v: 239.3, width: 166.874222, height: 12 },
      { page: 2, h: 378.7, v: 222.7, width: 36, height: 8.9 },
      { page: 2, h: 310.605225, v: 224.3, width: 166.874222, height: 12 },
      { page: 2, h: 378.7, v: 239.3, width: 36, height: 12 },
      { page: 2, h: 378.7, v: 224.3, width: 36, height: 12 },
      { page: 2, h: 378.7, v: 209.4, width: 36, height: 12 }
    ],
    expectCount: 6,
    expectNotContains: (r) => r.width > 160 // 整栏宽行盒必须全部消失
  },
  {
    name: 'L26 跨栏断点行：左栏尾行 + 右栏首行行盒全保留（不误杀）',
    input: [
      { page: 1, h: 310.6, v: 137.0, width: 166.87, height: 9 },
      { page: 1, h: 133.77, v: 256.0, width: 166.87, height: 9 },
      { page: 1, h: 310.6, v: 149.0, width: 166.87, height: 9 }
    ],
    expectCount: 3
  },
  {
    name: 'L30 行内复杂分数：行盒被 v-包含的公式碎片取代（更精确定位）',
    input: [
      { page: 1, h: 311.0, v: 212.0, width: 166.87, height: 15 },
      { page: 1, h: 311.0, v: 196.0, width: 166.87, height: 9 },
      { page: 1, h: 449.0, v: 212.0, width: 25, height: 6 },
      { page: 1, h: 448.0, v: 212.0, width: 27, height: 15 },
      { page: 1, h: 449.0, v: 204.0, width: 25, height: 6 }
    ],
    expectCount: 3,
    expectNotContains: (r) => r.width > 160 // 行盒不再整行高亮
  },
  {
    name: '守卫：行盒在 A 栏、碎片在 B 栏（横向无重叠）→ 行盒保留',
    input: [
      { page: 1, h: 133.77, v: 200.0, width: 166.87, height: 9 }, // 左栏行盒
      { page: 1, h: 449.0, v: 200.0, width: 25, height: 6 } // 右栏碎片，同 v
    ],
    expectCount: 2
  },
  {
    name: '防御：空数组 / 非法输入原样返回',
    input: [],
    expectCount: 0,
    rawPassthrough: true
  }
]

let failed = 0
for (const c of cases) {
  const out = dis(c.input)
  const okCount = out.length === c.expectCount
  let okExtra = true
  if (c.expectContains && !out.some(c.expectContains)) okExtra = false
  if (c.expectNotContains && out.some(c.expectNotContains)) okExtra = false
  if (c.rawPassthrough && out !== c.input) okExtra = false
  const pass = okCount && okExtra
  if (!pass) failed++
  console.log(
    (pass ? 'PASS' : 'FAIL') +
      '  ' + c.name + '  (输出 ' + out.length + ' 个, 期望 ' + c.expectCount + ')'
  )
  if (!pass) {
    console.log('  输出:', JSON.stringify(out))
  }
}
process.exit(failed ? 1 : 0)
