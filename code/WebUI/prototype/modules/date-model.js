/* ============================================================
 * PiggyDateModel —— 日期网格 + 农历/节日/手动节假日元数据合并（M0-6 / M0-7）
 * 在 M0-5 六周网格算法上，依次合并：PiggyLunar 农历与传统节日、
 * PiggyManualStore 手动放假/补班，输出每格元数据供 Vue 模板与配色消费。
 * 依赖：../modules/lunar-service.js、../modules/manual-store.js。无网络请求。
 * 对外：window.PiggyDateModel.buildCells(year, month0, today)
 * ============================================================ */
(function () {
  'use strict';

  function sameDay(a, b) {
    return a.getFullYear() === b.getFullYear()
      && a.getMonth() === b.getMonth()
      && a.getDate() === b.getDate();
  }

  /* v0.1.6.44：节日名称归一（数据源简称 → 统一全称）
     在线节假日数据用「中秋 / 端午 / 清明」这类简称，农历库与手动录入用「中秋节 / 端午节」，
     同一天两处名字不一致（用户 2026-11-06 指出的「一个叫中秋，一个叫中秋节」）。
     节假日批次名与节日展示名都过这张表，保证同一个节日只有一种写法。 */
  var FESTIVAL_NAME_FIX = {
    '中秋': '中秋节',
    '端午': '端午节',
    '清明': '清明节',
    '元宵': '元宵节',
    '七夕': '七夕节',
    '重阳': '重阳节',
    '腊八': '腊八节',
    '除夕': '除夕',
    '国庆': '国庆节'
  };

  function normalizeFestivalName(name) {
    if (!name) return name;
    return Object.prototype.hasOwnProperty.call(FESTIVAL_NAME_FIX, name) ? FESTIVAL_NAME_FIX[name] : name;
  }

  /**
   * 生成固定 6 周（42 格）月网格；周一开头。
   * @param {number} year   查看年
   * @param {number} month0 查看月，0-based
   * @param {Date} today    真实今天（用于今日标记）
   * @param {object} [opts] { lunarEnabled, festivalEnabled, jieqiEnabled }
   *        lunarEnabled 关闭时隐藏纯农历日副标题；festivalEnabled / jieqiEnabled 控制
   *        「额外显示」两类内容是否占位（角标与底色不受影响）
   * @returns {Array<object>} 每格元数据，字段见下方工厂对象
   */
  function buildCells(year, month0, today, opts) {
    var lunarEnabled = !opts || opts.lunarEnabled !== false;
    var festivalEnabled = !opts || opts.festivalEnabled !== false;
    var jieqiEnabled = !opts || opts.jieqiEnabled !== false;
    var offset = (new Date(year, month0, 1).getDay() + 6) % 7; // getDay 0=周日 → 周一开头偏移
    var start = new Date(year, month0, 1 - offset);
    var arr = [];

    /* v0.1.6.54：批次「正日」是否成立。远程 2027 年把清明正日误标在 4/3（当年清明节气在 4/5），
       名字与「正日」身份一起回退——该天按假期其余天处理（显示农历、进色带、跟随颜色），
       避免出现「农历孤格」：既不显名、又不进带。 */
    function useKeyDayName(st, cell) {
      if (!st.isKeyDay || !cell.manualName) return false;
      if (cell.manualName === '清明节' && cell.jieqi !== '清明') return false;
      return true;
    }

    for (var i = 0; i < 42; i++) {
      var d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
      var inMonth = d.getMonth() === month0;
      var isWeekend = d.getDay() === 0 || d.getDay() === 6;
      var isToday = sameDay(d, today);

      var cell = {
        // —— M0-5 已有 ——
        key: d.getFullYear() + '-' + d.getMonth() + '-' + d.getDate(),
        // v0.1.6.60：真实日期 yyyy-MM-dd（上面 key 用的是 0 基月，直接交给提醒桥会整体错一个月）
        ymd: d.getFullYear() + '-' + (d.getMonth() + 1 < 10 ? '0' : '') + (d.getMonth() + 1) + '-' + (d.getDate() < 10 ? '0' : '') + d.getDate(),
        day: d.getDate(),
        // v0.1.6.61：周几（1=周一…7=周日）。周期提醒「每周」按 weekdays('1,3,5') 匹配某一天要用到
        wday: ((d.getDay() + 6) % 7) + 1,
        inMonth: inMonth,
        isWeekend: isWeekend,
        isToday: isToday,

        // —— M0-6：农历 / 传统节日 ——
        lunarDay: null,          // 农历日名；非本月格为 null
        festival: null,         // 传统节日显示名
        festivalKind: null,     // 'lunar' 传统节日 / 'manual' 手动放假
        jieqi: null,            // v0.1.6.37：二十四节气名（非节气日为 null）
        subKind: null,          // v0.1.6.37：副标题底卡归属 'festival' | 'jieqi'，决定用哪套颜色/形态

        // —— M0-7：手动放假 / 补班 ——
        isOff: false,
        isAdjustWork: false,
        manualName: null,
        dataSource: null,

        // —— 日期类型：workday / weekend / holiday / adjustWork ——
        dayType: 'workday',

        // —— v0.1.6.46：假期区间里「非正日」的日期（只跟随颜色、不套形态）——
        isHolidayRest: false,

        // —— 右上角角标：'休' / '班' / null ——
        badge: null,

        // —— 渲染文案 ——
        subText: ''
      };

      if (inMonth) {
        // 1) 农历 / 传统节日
        var info = null;
        if (window.PiggyLunar && typeof window.PiggyLunar.getDayInfo === 'function') {
          info = window.PiggyLunar.getDayInfo(d.getFullYear(), d.getMonth(), d.getDate());
        }
        if (info) {
          cell.lunarDay = info.lunarDay;
          cell.lunarMonth = info.lunarMonth; // v0.1.6.24：农历仅初一显月份，其余普通农历日不显
          cell.jieqi = info.jieqi || null;   // v0.1.6.37：节气名
          if (info.festival) {
            cell.festival = normalizeFestivalName(info.festival);
            cell.festivalKind = 'lunar';
          }
        }

        // 2) 手动 / 远程节假日（M0-8b 双分区；store 内部先 manual 后 remote，手动优先，不被远程覆盖）
        var m = { isOff: false, isAdjustWork: false, manualName: null, isKeyDay: false, source: null };
        if (window.PiggyManualStore && typeof window.PiggyManualStore.status === 'function') {
          m = window.PiggyManualStore.status(d.getFullYear(), d.getMonth() + 1, d.getDate());
        }
        cell.isOff = !!m.isOff;
        cell.isAdjustWork = !!m.isAdjustWork;
        cell.manualName = normalizeFestivalName(m.manualName);
        cell.dataSource = m.source; // 'manual' / 'remote' / null（M0-10 管理页与 M0-8c 状态展示用）

        // 3) dayType / 副标题 / 角标裁决
        // 放不放假由法定批次（手动 > 远程）决定；传统节日仅作副标题展示名，不改变底色、不挂休标。
        // 优先级：补班 > 手动放假 > 远程放假 > 节日(仅展示) > 节气(仅展示) > 周末 > 工作日
        // v0.1.6.37：节气与节日同一天时让位给节日（清明等）；节日/节气开关只控副标题，角标与底色不受影响
        if (cell.isAdjustWork) {
          cell.dayType = 'adjustWork';
          cell.badge = '班';
          cell.subText = '班';                       // 补班只显「班」，不叠农历
        } else if (cell.isOff) {
          cell.dayType = 'holiday';
          cell.festivalKind = 'manual';
          cell.badge = '休';
          // 正日显节日/批次名，区间其余天显农历；库内节日名（除夕/春节/端午/中秋）优先
          // v0.1.6.49（P6）：法定假日正日的名称不再受「额外显示·节日与纪念日」开关影响——
          // 该开关只负责展示型节日（重阳节/万圣节等），关掉它不该让 10/1 的「国庆节」消失。
          if (cell.festival) {
            cell.subText = cell.festival;
            cell.subIsName = true; // v0.1.6.20：节日名加底卡
            cell.subKind = 'festival';
          /* v0.1.6.54：清明节名称只认节气日。远程批次 2027 年把清明正日误标在 4/3（当年节气清明在 4/5），
             两条通道各出一次「清明节」。批次正日名是「清明」而当天不是节气清明时，不采用该名，回落农历。 */
          } else if (useKeyDayName(m, cell)) {
            cell.subText = cell.manualName;
            cell.subIsName = true; // v0.1.6.20：节日名加底卡
            cell.subKind = 'festival';
          } else {
            cell.subText = lunarEnabled ? (cell.lunarDay || '') : '';
          }
          /* v0.1.6.46：假期区间「非正日」标记——只跟随颜色，不套形态（用户 2026-11-06 定稿）。
             正日 = 显节日名的那天 或 手动/远程批次标的关键日（isKeyDay）。 */
          cell.isHolidayRest = !(cell.subIsName || useKeyDayName(m, cell));
          /* v0.1.6.52：假期色带占第一行后，同一天还勾选的「节气 / 展示型节日」不再被吞掉，
             移到色带下方作第二行（用户 2026-11-09 定稿：上下排列两个）。
             主行已经用掉节日名时不再重复；节气当天若是清明，按节日样式显示（与现有口径一致）。 */
          cell.sub2Text = '';
          cell.sub2IsName = false;
          cell.sub2Kind = '';
          if (festivalEnabled && cell.festival && !cell.subIsName) {
            cell.sub2Text = cell.festival;
            cell.sub2IsName = true;
            cell.sub2Kind = 'festival';
          } else if (jieqiEnabled && cell.jieqi && !(cell.subIsName && cell.subText && cell.subText.indexOf(cell.jieqi) === 0)) {
            /* 清明当天主行已经是「清明节」，第二行不再重复同名的节气 */
            cell.sub2Text = cell.jieqi;
            cell.sub2IsName = true;
            cell.sub2Kind = cell.jieqi === '清明' ? 'festival' : 'jieqi';
          }
        } else if (festivalEnabled && cell.festival) {
          // 展示型节日：底色随普通工作日/周末，不挂角标
          cell.dayType = isWeekend ? 'weekend' : 'workday';
          cell.subText = cell.festival;
          cell.subIsName = true; // v0.1.6.20：节日名加底卡
          cell.subKind = 'festival';
        } else if (jieqiEnabled && cell.jieqi) {
          // 展示型节气：清明虽走节气通道，但按节日样式显示（用户 2026-11-06 定稿）
          cell.dayType = isWeekend ? 'weekend' : 'workday';
          cell.subText = cell.jieqi;
          cell.subIsName = true;
          cell.subKind = cell.jieqi === '清明' ? 'festival' : 'jieqi';
        } else if (isWeekend) {
          cell.dayType = 'weekend';
          // v0.1.6.28：普通农历日恢复显示日名（初二…三十），初一仍显月份（"十月"）
          cell.subText = lunarEnabled ? (cell.lunarDay === '初一' ? (cell.lunarMonth ? cell.lunarMonth + '月' : cell.lunarDay) : (cell.lunarDay || '')) : '';
        } else {
          cell.dayType = 'workday';
          cell.subText = lunarEnabled ? (cell.lunarDay === '初一' ? (cell.lunarMonth ? cell.lunarMonth + '月' : cell.lunarDay) : (cell.lunarDay || '')) : '';
        }
      } else {
        /* v0.1.6.90：非本月补格也要判日期类型——「每个工作日」提醒在补格上原来恒按周一到五兜底，
           补班（周末上班）会被漏掉、法定假反被算成工作日。只读节假日 store，不碰农历/节日文案/角标。 */
        var m2 = { isOff: false, isAdjustWork: false };
        if (window.PiggyManualStore && typeof window.PiggyManualStore.status === 'function') {
          m2 = window.PiggyManualStore.status(d.getFullYear(), d.getMonth() + 1, d.getDate());
        }
        cell.isOff = !!m2.isOff;
        cell.isAdjustWork = !!m2.isAdjustWork;
        cell.dayType = cell.isAdjustWork ? 'adjustWork' : (cell.isOff ? 'holiday' : (isWeekend ? 'weekend' : 'workday'));
      }

      arr.push(cell);
    }

    return arr;
  }

  window.PiggyDateModel = { buildCells: buildCells, normalizeFestivalName: normalizeFestivalName };
})();
