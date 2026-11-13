/* ============================================================
 * PiggyPanelStore —— 设置 / 工具 / 关于 面板数据与桥接（M0-9 ~ M0-11）
 * 职责：静态导航/卡片/文案定义（供 Node 回归校验）+ 桥调用封装（开关读写、同步状态、立即同步）。
 * 视图渲染在 panel.html；本模块不直接操作 DOM。
 * 桥方法沿用小组件同一 CSharpBridge 实例。
 * ============================================================ */
(function () {
  'use strict';

  /* ---------- 静态定义（纯数据，可在 Node 下直接校验） ---------- */

  // 设置窗左侧竖向导航：顺序即显示顺序（v0.1.6.74：小组件样式 → 日期样式 → 节假日管理 → 系统设置，四者图标互不相同）。
  var SETTINGS_NAV = [
    { id: 'appearance', title: '小组件样式', icon: 'widget3', desc: '透明度与位置锁定' },
    { id: 'dateStyle', title: '日期样式', icon: 'palette', desc: '底色、节日名称与提醒标记的样式' },
    { id: 'holidays', title: '节假日管理', icon: 'calendar', desc: '在线同步与手动添加节假日' },
    { id: 'system', title: '系统设置', icon: 'monitor', desc: '开机自启' }
  ];

  // 节假日管理页内部两个分区（在线同步已并入此处）。
  var HOLIDAY_SECTIONS = [
    { id: 'online', title: '在线同步', desc: '年度官方节假日安排在线拉取与状态' },
    { id: 'manual', title: '手动添加节假日', desc: '自建放假 / 补班批次（规划中）' }
  ];

  // 系统分组开关。
  var SYSTEM_ITEMS = [
    {
      key: 'autoStart', label: '开机自启',
      desc: '登录 Windows 后自动启动小猪日历（HKCU 注册表 Run 键）',
      getter: 'GetAutostartEnabled', setter: 'SetAutostartEnabled'
    }
  ];

  // 在线同步分组开关。
  var SYNC_ITEMS = [
    {
      key: 'sync', label: '在线节假日同步',
      desc: '启动后静默拉取当年 / 次年法定节假日，失败保留本地缓存',
      getter: 'GetSyncEnabled', setter: 'SetSyncEnabled'
    }
  ];

  // 工具中心：4 列 × 2 行网格；enabled=true 可进入骨架子页，false 为「敬请期待」占位不可点；icon 为 Reicon 图标名。
  var TOOL_CARDS = [
    { id: 'auto-shutdown', title: '自动关机', desc: '定时关机（第二期实现）', enabled: true, placeholder: false, icon: 'power-off' },
    { id: 'ph-2', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-3', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-4', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-5', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-6', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-7', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' },
    { id: 'ph-8', title: '敬请期待', desc: '功能规划中', enabled: false, placeholder: true, icon: 'hourglass' }
  ];

  // 时钟显示模式选项（设置页「系统」分组下）：实时更新（默认）/ 按分钟更新。
  var CLOCK_OPTIONS = {
    key: 'clockRealTime',
    getter: 'GetClockRealTime',
    setter: 'SetClockRealTime',
    label: '时钟显示',
    desc: '小组件时间刷新方式',
    choices: [
      { value: true, label: '实时更新', desc: '后台也实时刷新秒数，时间始终准确' },
      { value: false, label: '按分钟更新', desc: '后台每分钟刷新，窗口激活时实时显示秒数' }
    ]
  };

  /* ---------- v0.1.6.42 统一色板（公共节假日 / 周末 / 调休 / 二十四节气 共用） ----------
     8 色：霁蓝（原「节日蓝」改名）+ 原 4 色 + 朱红（原调休红）+ 烟灰 + 砚黑（非彩色档，随主题自适应）。
     颜色键与壳层 AppConfig.ClampColorKey 白名单一一对应。 */
  var PALETTE = [
    /* v0.1.6.63：新增「紫晶」——「提醒标记色」默认值（与旧硬编码 #8b7ff5 同值，视觉零变化） */
    { key: 'iris', label: '紫晶', hex: '#8B7FF5' },
    { key: 'jilan', label: '霁蓝', hex: '#3D6ED8' },
    { key: 'teal', label: '青碧', hex: '#2FB6A3' },
    { key: 'green', label: '竹青', hex: '#4C9A74' },   /* v0.1.6.57：周末绿换竹青（原 #3FA942 用户反馈「太土」）；键名仍是 green，config.ini 不需迁移 */
    { key: 'gold', label: '茶金', hex: '#D8A73B' },
    { key: 'orange', label: '暖橙', hex: '#F08A4B' },
    { key: 'rose', label: '玫红', hex: '#E4699B' },
    { key: 'vermilion', label: '朱红', hex: '#E0524D' },
    { key: 'gray', label: '烟灰', hex: '#8A909B' },
    { key: 'neutral', label: '砚黑', hex: '#24272E' }
  ];

  /* 「额外显示」卡：两组，每组 开关 + 颜色与样式折叠（只控制日期下方的名称，不动日期背景） */
  var EXTRA_GROUPS = [
    { id: 'festival', en: 'festivalEnabled', label: '节日与纪念日', color: 'festivalColor', style: 'festivalStyle', nameOnly: true },
    { id: 'jieqi', en: 'jieqiEnabled', label: '二十四节气', color: 'jieqiColor', style: 'jieqiStyle', nameOnly: true }
  ];

  /* 「假日/调休样式」卡：三组，每组 开关 + 关系项（仅周末有）+ 颜色与样式折叠 */
  var HOLIDAY_GROUPS = [
    { id: 'holiday', label: '公共节假日', desc: '放假那天的日期背景', en: 'holidayBgEnabled' },
    { id: 'weekend', label: '周末', desc: '周六、周日', en: 'weekendBgEnabled', mode: 'weekendMode' },
    { id: 'adjust', label: '调休上班', desc: '补班日期', en: 'adjustBgEnabled' }
  ];

  // 内置常用节日候选（手动录入名称下拉；名称限 8 字以内）。
  var MANUAL_FESTIVALS = [
    '春节', '元宵节', '清明节', '劳动节', '端午节', '中秋节', '国庆节', '元旦',
    '儿童节', '七夕节', '重阳节', '母亲节', '父亲节'
  ];

  // 关于页静态文案（版本号由桥 GetVersion 动态填充）。
  var ABOUT = {
    name: '小猪日历 PiggyCalendar',
    intro: 'Windows桌面悬浮日历小工具，带自动关机，设置提醒功能。',
    github: 'https://github.com/gggg312/piggy-calendar',
    gitee: 'https://gitee.com/gaopeng262/piggy-calendar',
    donate: '若您觉得好用，可自愿微信扫码赞助，支持后续更新维护',
    qrcode: 'assets/赞赏码_small.jpg'
  };

  var PanelDefs = {
    SETTINGS_NAV: SETTINGS_NAV,
    HOLIDAY_SECTIONS: HOLIDAY_SECTIONS,
    SYSTEM_ITEMS: SYSTEM_ITEMS,
    SYNC_ITEMS: SYNC_ITEMS,
    CLOCK_OPTIONS: CLOCK_OPTIONS,
    EXTRA_GROUPS: EXTRA_GROUPS,
    PALETTE: PALETTE,
    HOLIDAY_GROUPS: HOLIDAY_GROUPS,
    TOOL_CARDS: TOOL_CARDS,
    MANUAL_FESTIVALS: MANUAL_FESTIVALS,
    ABOUT: ABOUT
  };

  /* ---------- 桥调用封装 ---------- */

  function bridge() { return (typeof window !== 'undefined') ? window.Bridge : null; }

  function invoke(name) {
    var b = bridge();
    if (!b || !b.invoke) return Promise.reject(new Error('桥不可用'));
    var args = Array.prototype.slice.call(arguments, 1);
    return b.invoke.apply(b, [name].concat(args));
  }

  /**
   * 统一结果文案（v0.1.6.33）：壳层失败约定返回 "err:原因"（半角/全角冒号、可有空格），
   * 界面只显示原因，绝不把 "err:" 前缀漏到弹窗上；成功且给了 okText 就返回 okText。
   */
  function msgOf(res, okText) {
    if (res === 'ok' || res === true) return okText || '';
    if (res === null || res === undefined) return '';
    return String(res).replace(/^\s*err\s*[:：]\s*/i, '').trim();
  }

  /** 桥结果是否是失败（"err" 开头）。 */
  function isErr(res) {
    return typeof res === 'string' && /^\s*err/i.test(res);
  }

  var state = {
    ready: false,
    settings: { autoStart: false, sync: true, clockRealTime: true },
    extra: {
      festivalEnabled: true, festivalColor: 'gold', festivalStyle: 'outline',
      jieqiEnabled: true, jieqiColor: 'rose', jieqiStyle: 'outline'
    },
    /* v0.1.6.42「假日样式」默认值 = 现状观感（节假日霁蓝实心圆 / 周末青碧 / 调休朱红 / 节气青碧边框） */
    holidayStyle: {
      holidayBgEnabled: true, holidayShape: 'rect', holidayFill: 'solid', holidayColor: 'jilan',
      holidayNameMode: 'follow', holidayNameColor: 'gray', holidayNameFill: 'outline',
      weekendBgEnabled: true, weekendMode: 'alone', weekendShape: 'round', weekendFill: 'solid', weekendColor: 'green',
      adjustBgEnabled: true, adjustShape: 'rect', adjustFill: 'outline', adjustColor: 'vermilion',
      reminderDotColor: 'iris'   /* v0.1.6.63：提醒标记色（日期紫点 + 图标角标共用） */
    },
    syncStatus: null,
    /* v0.1.6.38 在线数据源 4 槽：桥未就绪时先用出厂默认占位，拿到 GetSyncSources 后覆盖 */
    syncSources: [
      { slot: 0, url: 'https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.json', enabled: true, defaultUrl: 'https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.json' },
      { slot: 1, url: 'https://unpkg.com/chinese-days/dist/years/{year}.json', enabled: true, defaultUrl: 'https://unpkg.com/chinese-days/dist/years/{year}.json' },
      { slot: 2, url: 'https://timor.tech/api/holiday/year/{year}', enabled: true, defaultUrl: 'https://timor.tech/api/holiday/year/{year}' },
      { slot: 3, url: '', enabled: false, defaultUrl: '' }
    ]
  };

  var PanelStore = {
    defs: PanelDefs,
    /** 供视图层统一剥 "err:" 前缀（v0.1.6.33）。 */
    msgOf: msgOf,

    /** 读取四个开关初始状态。 */
    init: function () {
      return this.refreshSettings().then(function () { state.ready = true; });
    },

    isReady: function () { return state.ready; },

    getSettings: function () { return state.settings; },

    /** 按分组定义重新拉取开关状态。 */
    refreshSettings: function () {
      var self = this;
      var reads = [];
      function read(item) {
        reads.push(invoke(item.getter).then(function (v) {
          state.settings[item.key] = (v === true || v === 'true' || v === 1);
        }).catch(function () { /* 桥缺失保持默认 */ }));
      }
      SYSTEM_ITEMS.forEach(read);
      SYNC_ITEMS.forEach(read);
      return Promise.all(reads).then(function () { return state.settings; });
    },

    /** 切换某个开关；成功后更新本地状态，失败回滚由调用方决定。 */
    toggle: function (key) {
      var all = SYSTEM_ITEMS.concat(SYNC_ITEMS);
      var item = null;
      for (var i = 0; i < all.length; i++) { if (all[i].key === key) { item = all[i]; break; } }
      if (!item) return Promise.reject(new Error('未知开关：' + key));
      var next = !state.settings[key];
      return invoke(item.setter, next).then(function (res) {
        if (isErr(res)) throw new Error(msgOf(res, '设置失败'));
        state.settings[key] = next;
        return state.settings;
      });
    },

    /** 拉取在线同步状态 JSON（GetSyncStatus）。 */
    refreshSyncStatus: function () {
      return invoke('GetSyncStatus').then(function (raw) {
        var s = null;
        try { s = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { s = null; }
        state.syncStatus = s;
        return s;
      }).catch(function () { state.syncStatus = null; return null; });
    },

    /** 手动立即同步（绕过状态机与退避）。 */
    syncNow: function () {
      return invoke('SyncNow');
    },

    /** 读取时钟显示模式（实时/按分钟）。 */
    getClockRealTime: function () {
      return invoke(CLOCK_OPTIONS.getter).then(function (v) {
        var real = (v === true || v === 'true' || v === 1);
        state.settings.clockRealTime = real;
        return real;
      }).catch(function () { state.settings.clockRealTime = true; return true; });
    },

    /** 切换时钟显示模式。 */
    setClockRealTime: function (val) {
      var real = !!val;
      return invoke(CLOCK_OPTIONS.setter, real).then(function (res) {
        if (isErr(res)) throw new Error(msgOf(res, '设置失败'));
        state.settings.clockRealTime = real;
        return state.settings;
      });
    },

    getVersion: function () {
      return invoke('GetVersion').then(function (v) { return v || '0.1.0.0'; }).catch(function () { return '0.1.0.0'; });
    },

    /* ==================== 手动节假日 CRUD（M0-8b 后端就绪，前端落地） ==================== */

    /** 读取手动批次列表（GetHolidayData → manual.batches）+ 在线 remote 元信息（供冲突检测）。 */
    getManualHolidays: function () {
      return invoke('GetHolidayData').then(function (raw) {
        var d = null;
        try { d = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { d = null; }
        return { batches: (d && d.manual && d.manual.batches) || [], remote: (d && d.remote) || null };
      }).catch(function () { return { batches: [], remote: null }; });
    },

    /** 新增手动批次；batch={kind,start,end,name}；返回 {ok,msg}。 */
    addManualBatch: function (batch) {
      return invoke('AddManualBatch', JSON.stringify(batch)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已保存' : msgOf(res, '保存失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** v0.1.6.37：读取「额外显示」配置（节日与纪念日 / 二十四节气）。 */
    refreshExtra: function () {
      return invoke('GetExtraDisplay').then(function (raw) {
        var d = typeof raw === 'string' ? JSON.parse(raw) : raw;
        if (d) {
          if (typeof d.festivalEnabled !== 'undefined') state.extra.festivalEnabled = !!d.festivalEnabled;
          if (d.festivalColor) state.extra.festivalColor = d.festivalColor;
          if (d.festivalStyle) state.extra.festivalStyle = d.festivalStyle === 'outline' ? 'outline' : 'solid';
          if (typeof d.jieqiEnabled !== 'undefined') state.extra.jieqiEnabled = !!d.jieqiEnabled;
          if (d.jieqiColor) state.extra.jieqiColor = d.jieqiColor;
          if (d.jieqiStyle) state.extra.jieqiStyle = d.jieqiStyle === 'solid' ? 'solid' : 'outline';
        }
        return state.extra;
      }).catch(function () { return state.extra; });
    },

    /** 当前「额外显示」配置副本（视图层渲染用）。 */
    getExtra: function () { return state.extra; },

    /** 更新「额外显示」某个键（enabled/color/style 三类）；返回 {ok,msg}。 */
    setExtra: function (patch) {
      var body = {};
      Object.keys(patch || {}).forEach(function (k) { body[k] = patch[k]; });
      return invoke('SetExtraDisplay', JSON.stringify(body)).then(function (res) {
        if (isErr(res)) return { ok: false, msg: msgOf(res, '设置失败') };
        Object.keys(patch || {}).forEach(function (k) {
          if (state.extra[k] !== undefined) state.extra[k] = patch[k];
        });
        return { ok: true, msg: '' };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /* ==================== v0.1.6.42 假日样式（公共节假日 / 周末 / 调休 / 节气） ==================== */

    /** 读取「假日样式」配置。 */
    refreshHolidayStyle: function () {
      return invoke('GetHolidayStyle').then(function (raw) {
        var d = null;
        try { d = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { d = null; }
        if (d) {
          Object.keys(state.holidayStyle).forEach(function (k) {
            if (typeof d[k] !== 'undefined' && d[k] !== null) {
              state.holidayStyle[k] = typeof state.holidayStyle[k] === 'boolean' ? !!d[k] : d[k];
            }
          });
        }
        return state.holidayStyle;
      }).catch(function () { return state.holidayStyle; });
    },

    getHolidayStyle: function () { return state.holidayStyle; },

    /** 更新「假日样式」任意键；返回 {ok,msg}。 */
    setHolidayStyle: function (patch) {
      var body = {};
      Object.keys(patch || {}).forEach(function (k) { body[k] = patch[k]; });
      return invoke('SetHolidayStyle', JSON.stringify(body)).then(function (res) {
        if (isErr(res)) return { ok: false, msg: msgOf(res, '设置失败') };
        Object.keys(patch || {}).forEach(function (k) {
          if (state.holidayStyle[k] !== undefined) state.holidayStyle[k] = patch[k];
        });
        return { ok: true, msg: '' };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /* ==================== v0.1.6.38 在线数据源 4 槽 ==================== */

    /** 读取 4 个在线源槽（地址 / 启用 / 出厂默认地址）。 */
    refreshSyncSources: function () {
      return invoke('GetSyncSources').then(function (raw) {
        var arr = null;
        try { arr = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { arr = null; }
        if (!arr || !arr.length) return state.syncSources;
        state.syncSources = arr;
        return state.syncSources;
      }).catch(function () { return state.syncSources; });
    },

    getSyncSources: function () { return state.syncSources; },

    /** 保存某个源槽（地址 + 启用）；返回 {ok,msg}。 */
    setSyncSource: function (slot, url, enabled) {
      return invoke('SetSyncSource', slot, url || '', !!enabled).then(function (res) {
        if (isErr(res)) return { ok: false, msg: msgOf(res, '保存失败') };
        state.syncSources.forEach(function (s) { if (s.slot === slot) { s.url = url || ''; s.enabled = !!enabled; } });
        return { ok: true, msg: '' };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 恢复出厂默认 4 槽。 */
    resetSyncSources: function () {
      return invoke('ResetSyncSources').then(function (res) {
        if (isErr(res)) return { ok: false, msg: msgOf(res, '恢复失败') };
        return { ok: true, msg: '' };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 测试某个源槽连通性；返回 {ok,code,ms,hasData,msg}。 */
    testSyncSource: function (slot) {
      return invoke('TestSyncSource', slot).then(function (raw) {
        var d = null;
        try { d = typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { d = null; }
        return d || { ok: false, msg: '测试无返回' };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 按 id 删除手动批次；返回 {ok,msg}。 */    removeManualBatch: function (id) {
      return invoke('RemoveManualBatch', id).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已删除' : msgOf(res, '删除失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 清空全部手动批次；返回 {ok,msg}。 */
    clearManual: function () {
      return invoke('ClearManual').then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已清空' : msgOf(res, '清空失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 手动批次按 keyDay 年份分组（keyDay 缺省取 start）；返回按年份升序的对象。 */
    groupManualByYear: function (batches) {
      var map = {};
      (batches || []).forEach(function (b) {
        var day = (b && (b.keyDay || b.start || b.end)) || '';
        var y = /^(\d{4})/.test(day) ? day.slice(0, 4) : '未知';
        (map[y] = map[y] || []).push(b);
      });
      var out = {};
      Object.keys(map).sort().forEach(function (k) { out[k] = map[k]; });
      return out;
    },

    /**
     * 冲突检测（仅班/休冲突）：手动批次与在线同日是否 kind 相反（off↔adjust）。
     * 返回冲突日期数组；同为 off 或同为 adjust 一致，不算冲突。
     */
    detectConflict: function (batch, remote) {
      var conflicts = [];
      var dates = (batch && batch.dates) || [];
      if (!remote || !remote.years || dates.length === 0) return conflicts;
      var byDate = {};
      var years = remote.years || {};
      Object.keys(years).forEach(function (yr) {
        var yy = years[yr];
        var batches = (yy && yy.batches) || [];
        batches.forEach(function (rb) {
          var rk = rb && rb.kind;
          var rd = (rb && rb.dates) || [];
          rd.forEach(function (dt) {
            (byDate[dt] = byDate[dt] || {})[rk] = true;
          });
        });
      });
      var bk = batch && batch.kind;
      dates.forEach(function (dt) {
        var cell = byDate[dt];
        if (!cell) return;
        var opp = (bk === 'off') ? cell.adjust : cell.off;
        if (opp) conflicts.push(dt);
      });
      return conflicts;
    },

    /* ==================== 自动关机（②） ==================== */

    /** 读取自动关机调度状态（v0.1.6.0 周期版：{enabled,time,repeatType,weekdays,monthDay,advanceMinutes,target,remainingSeconds}）。 */
    getAutoShutdownStatus: function () {
      return invoke('GetAutoShutdownStatus').then(function (raw) {
        try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { return null; }
      }).catch(function () { return null; });
    },

    /** 设置自动关机（周期版）：cfg={enabled,time:'HH:mm',repeatType:'daily'|'weekly'|'monthly',weekdays:'1,3,5',monthDay,advanceMinutes}；返回 {ok,msg}。 */
    setAutoShutdown: function (cfg) {
      return invoke('SetAutoShutdown', JSON.stringify(cfg)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已设置' : msgOf(res, '设置失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 取消自动关机；返回 {ok,msg}。 */
    cancelAutoShutdown: function () {
      return invoke('CancelAutoShutdown').then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已取消' : msgOf(res, '取消失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /* ==================== 提醒（④ v0.1.6.23） ==================== */

    /** 列出全部提醒；返回数组 [{id,mode,text,sound,repeatType,triggerLabel,...}]。 */
    listReminders: function () {
      return invoke('ListReminders').then(function (raw) {
        try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { return []; }
      }).catch(function () { return []; });
    },

    /** 添加提醒：cfg={mode:'countdown'|'datetime',text,sound,repeatType,countdownMinutes?,datetimeTs?}；返回 {ok,msg}。 */
    addReminder: function (cfg) {
      return invoke('AddReminder', JSON.stringify(cfg)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已添加' : msgOf(res, '添加失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 删除提醒：id；返回 {ok,msg}。 */
    removeReminder: function (id) {
      return invoke('RemoveReminder', String(id)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已删除' : msgOf(res, '删除失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /** 修改提醒：cfg={id,...}；返回 {ok,msg}。 */
    updateReminder: function (cfg) {
      return invoke('UpdateReminder', JSON.stringify(cfg)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已保存' : msgOf(res, '保存失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    },

    /* ==================== 小组件外观（③：透明度 + 位置锁定） ==================== */

    /** 读取外观配置（{locked, opacityPercent}）。 */
    getWidgetAppearance: function () {
      return invoke('GetWidgetAppearance').then(function (raw) {
        try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { return null; }
      }).catch(function () { return null; });
    },

    /** 更新外观配置：cfg={locked?} 或 {opacityPercent?}；返回 {ok,msg}。 */
    setWidgetAppearance: function (cfg) {
      return invoke('SetWidgetAppearance', JSON.stringify(cfg)).then(function (res) {
        return { ok: res === 'ok', msg: res === 'ok' ? '已保存' : msgOf(res, '保存失败') };
      }).catch(function (e) { return { ok: false, msg: '桥调用失败：' + e.message }; });
    }
  };

  // 暴露给 Node 回归（无 window 时仅暴露静态定义）。
  if (typeof module !== 'undefined' && module.exports) {
    module.exports = { PanelDefs: PanelDefs };
  }
  if (typeof window !== 'undefined') {
    window.PiggyPanelStore = PanelStore;
  }
})();
