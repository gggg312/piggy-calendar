/* ============================================================
 * PiggySampleHolidays2027 —— 2027 内置节假日数据
 * 与 2026 内置样本同结构，用于首次运行即具备当年放假/补班信息，
 * 断网、在线同步未拉到时仍可正常显示。运行期与 remote 分区各自索引，互不覆盖。
 * 来源：国务院办公厅 2027 年放假安排（2026-11-05 公布），已与软件在线同步
 *       落盘数据（运行组件\manual-holidays.json 的 remote.years.2027）逐条核对一致。
 *       kind: off=放假, adjust=调休补班；dates 由 manual-store 按 start~end 展开落盘。
 *       keyDay 沿用同步数据口径（春节批次取 2027-02-05 除夕）。
 * ============================================================ */
(function () {
  'use strict';

  var batches = [
    // —— 放假区间 ——
    { id: '2027-yuandan',   kind: 'off', name: '元旦',   keyDay: '2027-01-01', start: '2027-01-01', end: '2027-01-03' },
    { id: '2027-chunjie',   kind: 'off', name: '春节',   keyDay: '2027-02-05', start: '2027-02-05', end: '2027-02-13' },
    { id: '2027-qingming',  kind: 'off', name: '清明',   keyDay: '2027-04-03', start: '2027-04-03', end: '2027-04-05' },
    { id: '2027-laodong',   kind: 'off', name: '劳动节', keyDay: '2027-05-01', start: '2027-05-01', end: '2027-05-05' },
    { id: '2027-duanwu',    kind: 'off', name: '端午',   keyDay: '2027-06-09', start: '2027-06-09', end: '2027-06-09' },
    { id: '2027-zhongqiu',  kind: 'off', name: '中秋',   keyDay: '2027-09-15', start: '2027-09-15', end: '2027-09-15' },
    { id: '2027-guoqing',   kind: 'off', name: '国庆节', keyDay: '2027-10-01', start: '2027-10-01', end: '2027-10-07' },
    // —— 调休补班（周末上班）——
    { id: '2027-chunjie-adjust-1',   kind: 'adjust', name: '春节',   start: '2027-01-31', end: '2027-01-31' },
    { id: '2027-chunjie-adjust-2',   kind: 'adjust', name: '春节',   start: '2027-02-14', end: '2027-02-14' },
    { id: '2027-laodong-adjust',     kind: 'adjust', name: '劳动节', start: '2027-05-08', end: '2027-05-08' },
    { id: '2027-guoqing-adjust-1',   kind: 'adjust', name: '国庆节', start: '2027-09-26', end: '2027-09-26' },
    { id: '2027-guoqing-adjust-2',   kind: 'adjust', name: '国庆节', start: '2027-10-09', end: '2027-10-09' }
  ];

  window.PiggySampleHolidays2027 = { version: 1, batches: batches };
})();
