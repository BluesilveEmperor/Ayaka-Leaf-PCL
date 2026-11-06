/*
 * Ayakaleaf 界面语言切换
 * ---------------------------------------------------------------------------
 * 在编辑器 文件 → 设置 → 拼写和语言 面板末尾添加“界面语言”选项。
 * Overleaf 的界面语言由访问端口决定（每个启用的语言绑定一个端口，
 * en 固定主端口），切换时先通知宿主持久化偏好，再跳转到目标语言端口的
 * 同一路径，行为与页脚语言选择器一致。
 *
 * 注入方式：WebView2 的 AddScriptToExecuteOnDocumentCreatedAsync，每次文档创建都执行。
 * __AYAKA_LANGUAGES_JSON__ 占位符由启动器在注入前替换为语言列表 JSON：
 * [{"code":"en","name":"English","port":8080},...]（仅含已启用的语言）。
 *
 * 前端结构可能随镜像版本变化，因此设置面板锚点使用 4 级回退链；
 * 全部缺失时只告警（并上报宿主），不抛异常，保证界面语言条目缺位不影响编辑器功能。
 */

(() => {
  'use strict'

  var LANGS = __AYAKA_LANGUAGES_JSON__

  var ROW_ID = 'setting-ayakaInterfaceLanguage'
  var SELECT_ID = 'ayakaInterfaceLanguage'
  var DEFAULT_SELECT_CLASS = 'form-select form-select-sm ide-dropdown-setting'

  // 仅作“脚本已在本文档执行过”的标记，用于避免重复注册 MutationObserver。
  // 是否需要注入一律以 DOM 中是否已存在目标行为准（前端路由重渲染会移除它）。
  var firstRun = !window.__ayakaInterfaceLanguage
  window.__ayakaInterfaceLanguage = true

  function currentLang() {
    for (var i = 0; i < LANGS.length; i++) {
      if (String(LANGS[i].port) === window.location.port) return LANGS[i]
    }
    return LANGS[0]
  }

  // 标签文案跟随当前界面语言（简体中文 / 其他语言用英文文案）
  var strings = function () {
    return currentLang().code === 'zh-CN'
      ? {
          title: '界面语言',
          desc: '切换 Overleaf 界面的显示语言',
        }
      : {
          title: 'Interface language',
          desc: 'Change the display language of the Overleaf interface',
        }
  }

  // 宿主消息通道：非 WebView2 环境（普通浏览器）没有 chrome.webview，静默跳过
  function postToHost(message) {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(message)
      }
    } catch (e) {
      console.warn('[Ayakaleaf] 向宿主发送消息失败：', e)
    }
  }

  function buildRow() {
    var s = strings()

    var row = document.createElement('div')
    row.id = ROW_ID
    row.className = 'ide-setting'

    var left = document.createElement('div')

    var label = document.createElement('label')
    label.setAttribute('for', SELECT_ID)
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
    select.id = SELECT_ID
    // 复用面板里现成下拉框的样式类，保证外观与其它设置项一致
    var anchor = document.getElementById('setting-spellCheckLanguage')
    var ref = anchor ? anchor.querySelector('select') : null
    select.className = ref && ref.className ? ref.className : DEFAULT_SELECT_CLASS

    var current = currentLang()
    for (var i = 0; i < LANGS.length; i++) {
      select.add(new Option(LANGS[i].name, String(LANGS[i].port)))
    }
    select.value = String(current.port)

    select.addEventListener('change', function () {
      var targetPort = select.value
      var target = null
      for (var i = 0; i < LANGS.length; i++) {
        if (String(LANGS[i].port) === targetPort) {
          target = LANGS[i]
          break
        }
      }
      if (!target) return
      // 1. 先通知宿主持久化偏好：WebView2 在文档卸载时可能丢弃后续消息，
      //    因此必须在任何导航动作之前发出，且导航要延迟到消息投递之后。
      postToHost({
        type: 'ayaka:setInterfaceLanguage',
        lang: target.code,
      })
      // 2. 已在目标端口时不跳转（避免产生重复历史记录）
      if (window.location.port === targetPort) {
        updateRowValue(row)
        return
      }
      // 3. 延迟 50ms 再跳转，给宿主消息留出投递时间；同路径仅换端口
      var url = new URL(window.location.href)
      if (url.port !== targetPort) {
        url.port = targetPort
        window.setTimeout(function () {
          window.location.href = url.href
        }, 50)
      }
    })

    right.appendChild(select)

    row.appendChild(left)
    row.appendChild(right)
    return row
  }

  function rowLabel(row) {
    return row.querySelector('label.ide-setting-title')
  }

  function rowDescription(row) {
    return row.querySelector('.ide-setting-description')
  }

  function rowSelect(row) {
    return row.querySelector('#' + SELECT_ID)
  }

  function updateRowValue(row) {
    var select = rowSelect(row)
    if (!select) return
    var want = String(currentLang().port)
    if (select.value !== want) select.value = want
  }

  // 已存在行时只更新文案与选中值（前端重渲染后行会被移除，由 tryInject 重新创建）
  function updateRow(row) {
    var s = strings()
    var label = rowLabel(row)
    if (label && label.textContent !== s.title) label.textContent = s.title
    var desc = rowDescription(row)
    if (desc && desc.textContent !== s.desc) desc.textContent = s.desc
    updateRowValue(row)
  }

  /*
   * 锚点回退链（顺序固定）：
   * 1. #setting-spellCheckLanguage 的 .ide-settings-section 祖先；
   * 2. data-setting-id="setting-spellCheckLanguage" 元素的 .ide-settings-section 祖先；
   * 3. 任意 .ide-settings-section 中带 “Spell” / “拼写” 文案的 label 所属分区；
   * 4. 全部失败返回 null（调用方负责告警与上报）。
   */
  function findSection() {
    var byId = document.getElementById('setting-spellCheckLanguage')
    if (byId) {
      var section = byId.closest('.ide-settings-section')
      if (section) return section
    }

    var byAttr = document.querySelector('[data-setting-id="setting-spellCheckLanguage"]')
    if (byAttr) {
      var attrSection = byAttr.closest('.ide-settings-section')
      if (attrSection) return attrSection
    }

    var sections = document.querySelectorAll('.ide-settings-section')
    for (var i = 0; i < sections.length; i++) {
      var labels = sections[i].querySelectorAll('label')
      for (var j = 0; j < labels.length; j++) {
        var text = labels[j].textContent || ''
        if (text.indexOf('Spell') >= 0 || text.indexOf('拼写') >= 0) return sections[i]
      }
    }

    return null
  }

  // 锚点缺失只上报一次（同一文档内即便脚本被重复执行也不会刷屏），
  // 注入成功后重置，便于前端结构变化导致锚点再次丢失时重新上报
  function reportMissingAnchor() {
    if (window.__ayakaLangMissingReported) return
    window.__ayakaLangMissingReported = true
    console.warn('[Ayakaleaf] 未找到“拼写和语言”设置锚点，界面语言条目未注入')
    postToHost({ type: 'ayaka:langAnchorMissing' })
  }

  function tryInject() {
    var existing = document.getElementById(ROW_ID)
    if (existing) {
      window.__ayakaLangMissingReported = false
      updateRow(existing)
      return
    }

    var section = findSection()
    if (!section) {
      reportMissingAnchor()
      return
    }

    window.__ayakaLangMissingReported = false
    section.appendChild(buildRow())
  }

  // 编辑器 DOM 变化频繁（CodeMirror 输入等），用 rAF 合并检查避免频繁查询
  var scheduled = false
  function schedule() {
    if (scheduled) return
    scheduled = true
    window.requestAnimationFrame(function () {
      scheduled = false
      try {
        tryInject()
      } catch (e) {
        // 注入失败不得影响 CodeMirror / PDF 等既有功能
        console.warn('[Ayakaleaf] 界面语言条目注入失败：', e)
      }
    })
  }

  try {
    if (firstRun) {
      var target = document.documentElement || document
      if (target && window.MutationObserver) {
        new MutationObserver(schedule).observe(target, { childList: true, subtree: true })
      }
    }
    tryInject()
  } catch (e) {
    console.warn('[Ayakaleaf] 界面语言条目初始化失败：', e)
  }
})()
