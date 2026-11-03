/*
 * Ayakaleaf 界面语言切换
 * ---------------------------------------------------------------------------
 * 在编辑器 文件 → 设置 → 拼写和语言 面板末尾添加“界面语言”选项。
 * Overleaf 的界面语言由访问端口决定（主端口 = English，主端口 + 1 = 简体中文），
 * 切换时跳转到另一端口的同一路径，行为与页脚语言选择器一致。
 *
 * 注入方式：WebView2 的 AddScriptToExecuteOnDocumentCreatedAsync，每次文档创建都执行。
 * __AYAKA_MAIN_PORT__ / __AYAKA_ZH_PORT__ 占位符由启动器在注入前替换为实际端口。
 */

(() => {
  'use strict'

  if (window.__ayakaInterfaceLanguage) return
  window.__ayakaInterfaceLanguage = true

  var MAIN_PORT = '__AYAKA_MAIN_PORT__'
  var ZH_PORT = '__AYAKA_ZH_PORT__'

  var isZh = function () {
    return window.location.port === ZH_PORT
  }

  // 标签文案跟随当前界面语言
  var strings = function () {
    return isZh()
      ? {
          title: '界面语言',
          desc: '切换 Overleaf 界面的显示语言',
        }
      : {
          title: 'Interface language',
          desc: 'Change the display language of the Overleaf interface',
        }
  }

  function buildRow() {
    var s = strings()

    var row = document.createElement('div')
    row.id = 'setting-ayakaInterfaceLanguage'
    row.className = 'ide-setting'

    var left = document.createElement('div')

    var label = document.createElement('label')
    label.setAttribute('for', 'ayakaInterfaceLanguage')
    label.className = 'ide-setting-title'
    label.textContent = s.title

    var desc = document.createElement('div')
    desc.className = 'ide-setting-description'
    desc.textContent = s.desc

    left.appendChild(label)
    left.appendChild(desc)

    var right = document.createElement('div')
    right.className = 'ide-setting-input'

    var select = document.createElement('select')
    select.id = 'ayakaInterfaceLanguage'
    // 复用面板里现成下拉框的样式类，保证外观与其它设置项一致
    var ref = document.querySelector('#setting-spellCheckLanguage select')
    select.className = ref ? ref.className : 'form-select form-select-sm ide-dropdown-setting'

    var optEn = new Option('English', MAIN_PORT)
    var optZh = new Option('简体中文', ZH_PORT)
    select.add(optEn)
    select.add(optZh)
    select.value = isZh() ? ZH_PORT : MAIN_PORT

    select.addEventListener('change', function () {
      var targetPort = select.value
      // 通知宿主持久化语言偏好（下次打开工作台沿用）
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({
          type: 'ayaka:setInterfaceLanguage',
          lang: targetPort === ZH_PORT ? 'zh' : 'en',
        })
      }
      // 跳转到另一端口的同一路径（与页脚语言选择器的跳转方式一致）
      var url = new URL(window.location.href)
      if (url.port !== targetPort) {
        url.port = targetPort
        window.location.href = url.href
      }
    })

    right.appendChild(select)

    row.appendChild(left)
    row.appendChild(right)
    return row
  }

  var row = null

  function tryInject() {
    // 已注入且未被 React 重渲染移除
    if (row && row.isConnected) return
    // 锚点：拼写检查语言设置行（位于 文件 → 设置 → 拼写和语言 面板）
    var anchor = document.getElementById('setting-spellCheckLanguage')
    if (!anchor) return
    var section = anchor.closest('.ide-settings-section')
    if (!section) return
    row = buildRow()
    section.appendChild(row)
  }

  // 编辑器 DOM 变化频繁（CodeMirror 输入等），用 rAF 合并检查避免频繁查询
  var scheduled = false
  function schedule() {
    if (scheduled) return
    scheduled = true
    window.requestAnimationFrame(function () {
      scheduled = false
      tryInject()
    })
  }

  var observer = new MutationObserver(schedule)
  observer.observe(document.documentElement, { childList: true, subtree: true })
  tryInject()
})()
