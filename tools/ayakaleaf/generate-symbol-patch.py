#!/usr/bin/env python3
"""Ayakaleaf 符号面板拓展补丁生成器。

把 LaTeX-Workshop 的符号面板数据（snippetpanel.json，10 分类 648 符号）转换成
Overleaf 原生符号面板 schema（category/command/codepoint/description/aliases/notes），
并生成自包含的 node 补丁脚本（Resources/ayakaleaf/symbol-patch.js）。

补丁脚本由启动器在 Ayakaleaf 服务启动时注入容器执行（gzip+base64 经 wslc exec），
替换 ide bundle 中的符号 JSON 载荷与分类函数：
- 原生 5 分类符号在补丁时从 bundle 现有载荷动态解析并重映射进 10 分类（镜像更新不丢符号）；
- LaTeX-Workshop 符号按其原始分类合并（同分类同命令去重，原生优先）；
- 分类标签固定中文（保留 LaTeX-Workshop 分类体系）。

codepoint 推导顺序：
1. 纯字符 snippet（定界符等）直接取字符；
2. MathJax SVG 首个字形 id 的十六进制 codepoint；
3. unimathsymbols.json 的 detail 首字符；
4. 重音命令 → 组合附加符；
5. 字母样式（\\mathbb{A} 等）→ 数学字母数字符号区；
6. 无法解析时留空，由补丁加固后的解析逻辑回退显示命令文本。

用法：
    python generate-symbol-patch.py [snippetpanel.json] [unimathsymbols.json]

默认输入路径指向本机 LaTeX-Workshop 检出目录，输出写入仓库资源目录。
"""

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PANEL = Path(r"D:\Documents\02_Programming\LaTeX-Workshop\resources\snippetview\snippetpanel.json")
DEFAULT_UNIMATH = Path(r"D:\Documents\02_Programming\LaTeX-Workshop\data\unimathsymbols.json")
OUTPUT = REPO_ROOT / "PCL.Core" / "Ayakaleaf" / "Assets" / "symbol-patch.js"

PATCH_VERSION = "v1"

# 分类体系：保留 LaTeX-Workshop 的分类，界面以中文显示（顺序即标签顺序）
CATEGORIES = [
    ("Greek/Hebrew Letters", "希腊/希伯来字母"),
    ("Delimiters", "定界符"),
    ("Maths Constructs", "数学结构"),
    ("Variable-sized symbols", "变长符号"),
    ("Standard Functions", "标准函数"),
    ("Operators and Relations", "运算符与关系"),
    ("Arrows", "箭头"),
    ("Accents", "重音符号"),
    ("Miscellaneous", "杂项"),
    ("Letter Styles", "字母样式"),
]

# 原生 5 分类 → LaTeX-Workshop 分类的归并映射（补丁时对 bundle 现有符号执行）
NATIVE_CATEGORY_MAP = {
    "Greek": "Greek/Hebrew Letters",
    "Arrows": "Arrows",
    "Operators": "Operators and Relations",
    "Relations": "Operators and Relations",
    "Misc": "Miscellaneous",
}

# 重音命令 → 组合附加符（LaTeX-Workshop "Accents" 分类的显示字符）
ACCENT_COMBINING = {
    "hat": 0x0302, "dot": 0x0307, "check": 0x030C, "ddot": 0x0308,
    "tilde": 0x0303, "breve": 0x0306, "acute": 0x0301, "bar": 0x0304,
    "grave": 0x0300, "vec": 0x20D7,
}

# 字母样式 → 数学字母数字符号区（U+1D400 起，含标准例外字符）
LETTER_STYLES = {
    "bb": {"upper": 0x1D538, "lower": 0x1D552, "digit": 0x1D7D8, "exc": {
        "C": 0x2102, "H": 0x210D, "N": 0x2115, "P": 0x2119, "Q": 0x211A, "R": 0x211D, "Z": 0x2124}},
    "cal": {"upper": 0x1D49C, "lower": 0x1D4B6, "digit": None, "exc": {
        "B": 0x212C, "E": 0x2130, "F": 0x2131, "H": 0x210B, "I": 0x2110, "L": 0x2112,
        "M": 0x2133, "R": 0x211B, "e": 0x212F, "g": 0x210A, "o": 0x2134}},
    "frak": {"upper": 0x1D504, "lower": 0x1D51E, "digit": None, "exc": {
        "C": 0x212D, "H": 0x210C, "I": 0x2111, "R": 0x211C, "Z": 0x2128}},
    "sf": {"upper": 0x1D5A0, "lower": 0x1D5BA, "digit": 0x1D7E2, "exc": {}},
    "tt": {"upper": 0x1D670, "lower": 0x1D68A, "digit": 0x1D7F6, "exc": {}},
    "bf": {"upper": 0x1D400, "lower": 0x1D41A, "digit": 0x1D7CE, "exc": {}},
}

MATHJAX_GLYPH = re.compile(r"#E1-MJ[A-Z0-9]*-([0-9A-F]{2,5})")
LETTER_STYLE_CMD = re.compile(r"^\\math(bb|cal|frak|scr|sf|tt|bf|rm|normal)\{([A-Za-z0-9])\}$")
PRIVATE_USE_A = 0xE000
PRIVATE_USE_B = 0xF8FF


def usable_codepoint(cp: int) -> bool:
    """可显示字符：可打印区（含 ASCII 标点/括号），排除控制符与 PUA。"""
    return 0x20 <= cp < 0x110000 and not (PRIVATE_USE_A <= cp <= PRIVATE_USE_B)


def printable_char(text: str):
    """取首个可打印字符（含 ASCII），无则 None。仅用于纯字符 snippet。"""
    for ch in text:
        cp = ord(ch)
        if 0x20 <= cp < 0x110000 and not (PRIVATE_USE_A <= cp <= PRIVATE_USE_B):
            return ch
    return None


def expand_snippet(snippet: str) -> str:
    """展开 LaTeX-Workshop 片段占位符为纯文本：${n:默认}→默认、$n→空、${TM_SELECTED_TEXT}→空。"""
    out = []
    i, n = 0, len(snippet)
    while i < n:
        c = snippet[i]
        if c == "$" and i + 1 < n and snippet[i + 1] == "{":
            depth = 0
            end = -1
            for j in range(i + 1, n):
                if snippet[j] == "{":
                    depth += 1
                elif snippet[j] == "}":
                    depth -= 1
                    if depth == 0:
                        end = j
                        break
            if end < 0:
                out.append(c)
                i += 1
                continue
            inner = snippet[i + 2:end]
            m = re.match(r"^(\d+)(?::([\s\S]*))?$", inner)
            if m:
                content = m.group(2) if m.group(2) is not None else ""
                content = content.replace("${TM_SELECTED_TEXT}", "")
                if "$" in content:
                    content = expand_snippet(content)
                out.append(content)
            elif inner == "TM_SELECTED_TEXT":
                pass
            else:
                out.append(snippet[i:end + 1])
            i = end + 1
            continue
        if c == "$" and i + 1 < n and snippet[i + 1].isdigit():
            j = i + 1
            while j < n and snippet[j].isdigit():
                j += 1
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


def first_char(text: str):
    """取首个可用显示字符，无则 None。"""
    for ch in text:
        cp = ord(ch)
        if usable_codepoint(cp):
            return ch
    return None


def derive_codepoint(name: str, source: str, snippet: str, svg: str, unimath, category: str = ""):
    """按推导链返回 (显示字符, 命中方式)。"""
    # 1. 纯字符 snippet（定界符、字母 o 等）
    if snippet and "$" not in snippet and "\\" not in snippet:
        ch = printable_char(snippet)
        if ch:
            return ch, "plain"
    # 2. 字母样式（判定须先于 SVG：MathJax 花体/黑板/哥特字体用字体局部码点，
    #    如 E1-MJCAL-41 实为花体 𝒜 而非罗马 A）
    m = LETTER_STYLE_CMD.match(source)
    if m:
        style, letter = m.group(1), m.group(2)
        if style in ("rm", "normal"):
            return letter, "letterstyle"
        table = LETTER_STYLES.get("cal" if style == "scr" else style)
        if table:
            if letter in table["exc"]:
                return chr(table["exc"][letter]), "letterstyle"
            base = table["digit"] if letter.isdigit() and table["digit"] else (
                table["upper"] if letter.isupper() else table["lower"])
            if base:
                offset = ord(letter) - (0x30 if letter.isdigit() else 0x41 if letter.isupper() else 0x61)
                return chr(base + offset), "letterstyle"
    # 3. 标准函数（\cos 等）非符号：直接回退为命令文本，避免误导性的首字母显示
    if category == "Standard Functions":
        return "", "text"
    # 4. MathJax SVG 首个字形
    m = MATHJAX_GLYPH.search(svg or "")
    if m:
        cp = int(m.group(1), 16)
        if usable_codepoint(cp):
            return chr(cp), "svg"
    # 5. unimathsymbols 数据库
    key = name if not name.startswith("\\") else name[1:]
    entry = unimath.get(key) or unimath.get(key.strip("{}"))
    if entry:
        ch = first_char(entry.get("detail", ""))
        if ch:
            return ch, "unimath"
    # 6. 重音组合附加符
    if key in ACCENT_COMBINING:
        return chr(ACCENT_COMBINING[key]), "accent"
    return "", "none"


def build_command(snippet: str) -> str:
    """插入命令 = 展开占位符；以字母结尾（裸命令）补一个空格防止吞字。"""
    cmd = expand_snippet(snippet)
    if re.search(r"[A-Za-z]$", cmd):
        cmd += " "
    return cmd


def build_symbols(panel: dict, unimath: dict):
    symbols = []
    dropped = []
    stats = {}
    seen = set()
    for raw_cat, items in panel["mathSymbols"].items():
        category = raw_cat.lstrip("-")
        for item in items:
            name = item.get("name", "")
            source = item.get("source", "")
            snippet = item.get("snippet", "")
            command = build_command(snippet)
            dedupe = (category, command.strip())
            if dedupe in seen:
                continue
            ch, method = derive_codepoint(name, source, snippet, item.get("svg", ""), unimath, category)
            stats[method] = stats.get(method, 0) + 1
            if method == "none":
                dropped.append((category, name or source))
            keywords = [k for k in (item.get("keywords", "") or "").split() if k]
            aliases = []
            for a in [*keywords, name, command.strip().lstrip("\\"), ch]:
                if a and a not in aliases:
                    aliases.append(a)
            symbols.append({
                "category": category,
                "command": command,
                "codepoint": f"U+{ord(ch):X}" if ch else "",
                "description": name or command.strip(),
                "aliases": aliases,
                "notes": "",
            })
            seen.add(dedupe)
    return symbols, stats, dropped


# node 补丁脚本模板。__AYAKA_*__ 占位符由本生成器替换。
PATCH_TEMPLATE = r'''#!/usr/bin/env node
/*
 * Ayakaleaf 符号面板拓展补丁（容器内执行：node symbol-patch.js）
 * ---------------------------------------------------------------------------
 * 拓展 Overleaf 原生符号面板：保留 LaTeX-Workshop 的 10 个分类（中文标签），
 * 符号集 = bundle 内现有原生符号（按分类归并）+ LaTeX-Workshop 全量符号。
 *
 * 对 /overleaf/services/web/public/js/pages/ide-*.js 做三处替换：
 *   R1 符号数据载荷（exports=JSON.parse('…')）→ 合并后的符号数组；
 *   R2 分类函数（category_greek…5 项）→ 10 个中文分类；
 *   R3 codepoint 解析加固（无效 codepoint 回退显示命令文本，防面板崩溃）。
 *
 * 安全措施：原始文件备份为 *.ayaka-orig，重复执行从备份基线重放（幂等）；
 * 写回前用 vm.Script 做语法校验，失败不落盘；任一替换失败整体放弃并还原。
 * 环境变量 AYAKA_WEB_PAGES 可覆盖 bundle 目录（测试用）。
 * 版本：__PATCH_VERSION__
 */
'use strict'
const fs = require('fs')
const path = require('path')
const vm = require('vm')

const CATEGORIES = __AYAKA_CATEGORIES__
const SYMBOLS = __AYAKA_SYMBOLS__
const NATIVE_CATEGORY_MAP = __AYAKA_NATIVE_MAP__
const MARKER = '/* ayaka-symbol-patch __PATCH_VERSION__ */'

const PAGES_DIR = process.env.AYAKA_WEB_PAGES || '/overleaf/services/web/public/js/pages'

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n')
}

// R1：替换符号 JSON 载荷。返回 {ok, source?, reason?, nativeCount?}
function replaceSymbols(source) {
  const key = '.exports=JSON.parse('
  const idx = source.indexOf(key)
  if (idx < 0) return { ok: false, reason: 'symbols-module-not-found' }
  const q = source.indexOf("'", idx + key.length)
  if (q < 0) return { ok: false, reason: 'payload-quote-not-found' }
  if (source.slice(q + 1, q + 15) !== '[{"category":"') {
    return { ok: false, reason: 'unexpected-payload' }
  }
  let end = q + 1
  for (;;) {
    end = source.indexOf("'", end)
    if (end < 0) return { ok: false, reason: 'payload-terminator-not-found' }
    if (source[end + 1] === ')') break
    end++
  }
  let native = []
  try {
    native = JSON.parse(source.slice(q + 1, end))
  } catch (e) {
    return { ok: false, reason: 'native-payload-parse-failed', error: String(e) }
  }
  // 原生符号重映射进 10 分类；LaTeX-Workshop 符号按（分类+命令）去重补充
  const merged = []
  const seen = new Set()
  for (const item of native) {
    const category = NATIVE_CATEGORY_MAP[item.category] || item.category
    merged.push(Object.assign({}, item, { category: category }))
    seen.add(category + '\u0000' + String(item.command || '').trim())
  }
  let added = 0
  for (const item of SYMBOLS) {
    const key2 = item.category + '\u0000' + String(item.command || '').trim()
    if (seen.has(key2)) continue
    merged.push(item)
    seen.add(key2)
    added++
  }
  const payload = JSON.stringify(merged).replace(/\\/g, '\\\\').replace(/'/g, "\\'")
  return { ok: true, source: source.slice(0, q + 1) + payload + source.slice(end), nativeCount: native.length, addedCount: added }
}

// R2：替换分类函数为 10 个中文分类。精确串优先，压缩器改名时回退正则。
function replaceCategories(source) {
  const exact = 'function(e){return[{id:"Greek",label:e("category_greek")},' +
    '{id:"Arrows",label:e("category_arrows")},' +
    '{id:"Operators",label:e("category_operators")},' +
    '{id:"Relations",label:e("category_relations")},' +
    '{id:"Misc",label:e("category_misc")}]}'
  const replacement = 'function(e){return' + JSON.stringify(CATEGORIES) + '}'
  if (source.indexOf(exact) >= 0) {
    return { ok: true, source: source.replace(exact, replacement) }
  }
  const re = /function\((\w+)\)\{return\[\{id:"Greek",label:\w+\("category_greek"\)\},\{id:"Arrows",label:\w+\("category_arrows"\)\},\{id:"Operators",label:\w+\("category_operators"\)\},\{id:"Relations",label:\w+\("category_relations"\)\},\{id:"Misc",label:\w+\("category_misc"\)\}\]\}/
  const m = source.match(re)
  if (m) return { ok: true, source: source.replace(m[0], replacement) }
  return { ok: false, reason: 'categories-function-not-found' }
}

// R3：codepoint 解析加固。解析失败时回退为命令文本（如 sin），面板不崩。
function hardenCodepointParse(source) {
  const re = /([A-Za-z$_][\w$]*)\.character=String\.fromCodePoint\(parseInt\(([A-Za-z$_][\w$]*)\.codepoint\.replace\(\/\^U\\\+0\*\/,""\),16\)\)/
  const m = source.match(re)
  if (!m) return { ok: false, source: source }
  const replacement = m[1] + '.character=(function(n,c){try{return String.fromCodePoint(parseInt(n.replace(/^U\\+0*/,""),16))}catch(e){return String(c).replace(/^\\\\/,"").trim()}})(' + m[2] + '.codepoint,' + m[2] + '.command)'
  return { ok: true, source: source.replace(m[0], replacement) }
}

function patchFile(file) {
  const current = fs.readFileSync(file, 'utf8')
  const backup = file + '.ayaka-orig'
  const baseline = fs.existsSync(backup) ? fs.readFileSync(backup, 'utf8') : current

  const r1 = replaceSymbols(baseline)
  if (!r1.ok) return { ok: false, file: file, reason: r1.reason, error: r1.error }
  const r2 = replaceCategories(r1.source)
  if (!r2.ok) return { ok: false, file: file, reason: r2.reason }
  const r3 = hardenCodepointParse(r2.source)
  let source = r3.ok ? r3.source : r2.source
  if (source.indexOf(MARKER) < 0) source += '\n' + MARKER

  if (source === current) return { ok: true, file: file, changed: false, nativeCount: r1.nativeCount, addedCount: r1.addedCount }

  try {
    new vm.Script(source)
  } catch (e) {
    return { ok: false, file: file, reason: 'syntax-check-failed', error: String(e) }
  }

  if (!fs.existsSync(backup)) fs.writeFileSync(backup, current)
  const tmp = file + '.ayaka-tmp'
  fs.writeFileSync(tmp, source)
  fs.renameSync(tmp, file)
  return { ok: true, file: file, changed: true, hardened: r3.ok, nativeCount: r1.nativeCount, addedCount: r1.addedCount }
}

function main() {
  let files
  try {
    files = fs.readdirSync(PAGES_DIR).filter(function (f) { return /^ide-.*\.js$/.test(f) })
  } catch (e) {
    return emit({ ok: false, reason: 'pages-dir-missing', error: String(e) })
  }
  if (files.length === 0) return emit({ ok: false, reason: 'ide-bundle-not-found' })
  const results = []
  for (const f of files) {
    try {
      results.push(patchFile(path.join(PAGES_DIR, f)))
    } catch (e) {
      results.push({ ok: false, file: f, reason: 'exception', error: String(e) })
    }
  }
  const failed = results.filter(function (r) { return !r.ok })
  emit({
    ok: failed.length === 0,
    changed: results.some(function (r) { return r.changed }),
    results: results,
  })
}

main()
'''


def js_json(value) -> str:
    """紧凑 JSON（ensure_ascii=False，中文原样），可直接作为 JS 字面量。"""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def render_patch_script(symbols) -> str:
    script = PATCH_TEMPLATE
    script = script.replace("__PATCH_VERSION__", PATCH_VERSION)
    script = script.replace("__AYAKA_CATEGORIES__", js_json(
        [{"id": cid, "label": label} for cid, label in CATEGORIES]))
    script = script.replace("__AYAKA_SYMBOLS__", js_json(symbols))
    script = script.replace("__AYAKA_NATIVE_MAP__", js_json(NATIVE_CATEGORY_MAP))
    return script


def main() -> int:
    panel_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_PANEL
    unimath_path = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_UNIMATH
    panel = json.loads(panel_path.read_text(encoding="utf-8"))
    unimath = json.loads(unimath_path.read_text(encoding="utf-8")) if unimath_path.exists() else {}

    symbols, stats, dropped = build_symbols(panel, unimath)
    script = render_patch_script(symbols)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(script, encoding="utf-8", newline="\n")

    print(f"符号总数：{len(symbols)}（codepoint 推导：{stats}）")
    per_cat = {}
    for s in symbols:
        per_cat[s["category"]] = per_cat.get(s["category"], 0) + 1
    for cid, label in CATEGORIES:
        print(f"  {label}（{cid}）：{per_cat.get(cid, 0)}")
    if dropped:
        print(f"无法推导显示字符（回退为命令文本）：{len(dropped)} 项")
        for cat, name in dropped[:20]:
            print(f"    {cat} / {name}")
    print(f"输出：{OUTPUT}（{OUTPUT.stat().st_size} 字节）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
