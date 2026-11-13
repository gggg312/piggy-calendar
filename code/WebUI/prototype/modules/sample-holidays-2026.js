/* ============================================================
 * PiggySampleHolidays2026 —— 2026 首批播种数据（M0-7 专用）
 * 仅用于首次运行时把 2026 放假/补班写入 manual-holidays.json；
 * M0-8 手动录入体系接管后本文件可移除。运行期不读取本文件，一律以 JSON 为准。
 * kind: off=放假, adjust=调休补班；dates 由 manual-store 按 start~end 展开落盘。
 * 来源：用户提供的 2026 年国务院放假通知，星期与区间天数已脚本核对一致。
 * ============================================================ */
(function () {
  'use strict';

  // kind: off=放假, adjust=调休补班；dates 由 manual-store 按 start~end 展开落盘。
  // keyDay：该批次在公历年中显批次名的正日（yyyy-MM-dd）；区间其余日期副标题显农历。
  // 春节批次内 2026-02-16 除夕、2026-02-17 春节由农历模块识别节日名，不占用 keyDay。
  var batches = [
    // —— 放假区间 ——
    { id: '2026-yuandan',      kind: 'off',    name: '元旦',   keyDay: '2026-01-01', start: '2026-01-01', end: '2026-01-03' },
    { id: '2026-chunjie',      kind: 'off',    name: '春节',   keyDay: '2026-02-17', start: '2026-02-15', end: '2026-02-23' },
    { id: '2026-qingming',     kind: 'off',    name: '清明节', keyDay: '2026-04-05', start: '2026-04-04', end: '2026-04-06' },
    { id: '2026-laodong',      kind: 'off',    name: '劳动节', keyDay: '2026-05-01', start: '2026-05-01', end: '2026-05-05' },
    { id: '2026-duanwu',        kind: 'off',    name: '端午节', keyDay: '2026-06-19', start: '2026-06-19', end: '2026-06-21' },
    { id: '2026-zhongqiu',     kind: 'off',    name: '中秋节', keyDay: '2026-09-25', start: '2026-09-25', end: '2026-09-27' },
    { id: '2026-guoqing',       kind: 'off',    name: '国庆节', keyDay: '2026-10-01', start: '2026-10-01', end: '2026-10-07' },
    // —— 调休补班（周末上班）——
    { id: '2026-yuandan-adjust',     kind: 'adjust', name: '', start: '2026-01-04', end: '2026-01-04' },
    { id: '2026-chunjie-adjust-1',   kind: 'adjust', name: '', start: '2026-02-14', end: '2026-02-14' },
    { id: '2026-chunjie-adjust-2',   kind: 'adjust', name: '', start: '2026-02-28', end: '2026-02-28' },
    { id: '2026-laodong-adjust',      kind: 'adjust', name: '', start: '2026-05-09', end: '2026-05-09' },
    { id: '2026-guoqing-adjust-1',   kind: 'adjust', name: '', start: '2026-09-20', end: '2026-09-20' },
    { id: '2026-guoqing-adjust-2',    kind: 'adjust', name: '', start: '2026-10-10', end: '2026-10-10' }
  ];

  window.PiggySampleHolidays2026 = { version: 1, batches: batches };
})();
