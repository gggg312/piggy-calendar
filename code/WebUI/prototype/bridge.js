/* ============================================================
 * 小猪日历 桥接层
 * 仅在 WebView2 壳内激活：window.chrome.webview.hostObjects.CSharpBridge
 * 纯浏览器打开原型时自动降级（window.Bridge = null），不影响原型预览。
 * ------------------------------------------------------------
 * 用法：
 *   Bridge.invoke('Ping')                  -> Promise，调 C# 方法
 *   Bridge.invoke('Method', arg1, arg2)   -> Promise，带参数
 *   Bridge.on('event-name', fn)           -> 订阅 C# 主动推送事件
 * ------------------------------------------------------------
 * 壳 → Web 事件（kind）：
 *   shell-ready          壳就绪，payload={version}
 *   widget-focus-state   小组件聚焦/失焦，payload={focused:true|false}
 *                        仅用于暂停/恢复秒刷新；失焦不收起窗口（v0.3 1.2）
 * ============================================================ */
(function () {
  var host = window.chrome && window.chrome.webview;
  var csharp = host && host.hostObjects && host.hostObjects.CSharpBridge;
  if (!host || !csharp) {
    window.Bridge = null; // 纯浏览器降级
    return;
  }
  window.Bridge = {
    available: true,
    invoke: function (name) {
      var fn = csharp[name];
      if (typeof fn !== 'function') {
        return Promise.reject(new Error('CSharpBridge.' + name + ' 不可用'));
      }
      var args = Array.prototype.slice.call(arguments, 1);
      try {
        // hostObjects 方法返回 Promise（异步），统一 Promise 化
        return Promise.resolve(fn.apply(csharp, args));
      } catch (e) {
        return Promise.reject(e);
      }
    },
    on: function (kind, fn) {
      host.addEventListener('message', function (e) {
        var m = e.data;
        if (m && m.kind === kind && typeof fn === 'function') fn(m.payload);
      });
    }
  };
  // 壳就绪事件：标记桥可用并写入版本号
  window.Bridge.on('shell-ready', function (payload) {
    document.documentElement.setAttribute('data-shell', 'ready');
    document.documentElement.setAttribute('data-shell-version', payload.version || '');
    if (payload && payload.version) {
      document.title = '小猪日历 v' + payload.version;
    }
  });
})();
