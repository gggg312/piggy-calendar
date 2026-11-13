/* ============================================================
 * PiggyNoticeParser —— 国家节假日通知文本识别（M0-8b 后端能力；预览/手改 UI 归 M0-10）
 * 输入整段通知文本 + 公历年，正则识别「放假区间」与「调休补班单日」，产出待确认批次候选。
 * 职责边界：只做文本 → 批次候选的解析；不写本地存储、不做冲突校验（由管理页处理）。
 * 识别规则面向当年国务院通知常见句式：只处理含「X月X日」的句子，其余（标题/文号/引导/鼓励/结尾）直接忽略；含日期但解析失败进入 warnings，不静默丢弃。
 * 对外：window.PiggyNoticeParser.parse(text, year) → { batches:[...], warnings:[...] }
 * ============================================================ */
(function () {
  'use strict';

  function pad(n) { return String(n).padStart(2, '0'); }
  function fmt(y, m, d) { return y + '-' + pad(m) + '-' + pad(d); }

  function isValidDate(y, m, d) {
    if (m < 1 || m > 12 || d < 1 || d > 31) return false;
    var dt = new Date(y, m - 1, d);
    return dt.getFullYear() === y && dt.getMonth() === m - 1 && dt.getDate() === d;
  }

  /** 展开 y 年 start(月,日)~end(月,日) 为 yyyy-MM-dd 数组；默认同年，跨公历年情形留 M1 扩充。 */
  function expand(y, sm, sd, em, ed) {
    var out = [];
    var cur = new Date(y, sm - 1, sd);
    var end = new Date(y, em - 1, ed);
    if (isNaN(cur.getTime()) || isNaN(end.getTime())) return out;
    // 区间回绕（end 早于 start，视为跨年）：本轮不支持，返回空由调用方告警
    if (end < cur) return out;
    while (cur <= end) {
      out.push(fmt(cur.getFullYear(), cur.getMonth() + 1, cur.getDate()));
      cur.setDate(cur.getDate() + 1);
    }
    return out;
  }

  /** 从句子前部提取节日名（如「春节：」「劳动节」）；取不到返回空串。 */
  function extractName(sentence, dateMatchIndex) {
    var head = sentence.slice(0, dateMatchIndex);
    var m = head.match(/([一-龥]{2,8})(?:节假日)?\s*[:：]?\s*$/);
    return m ? m[1] : '';
  }

  /**
   * 解析通知文本。
   * @param {string} text 整段通知
   * @param {number} year 归属公历年
   * @returns {{batches: Array<object>, warnings: Array<string>}}
   */
  function parse(text, year) {
    var batches = [];
    var warnings = [];
    if (!text || typeof text !== 'string') return { batches: batches, warnings: ['空文本'] };
    if (!year) year = new Date().getFullYear();

    // 按句号、分号、换行、问号分句；保留句内日期片段
    var sentences = text.split(/[。;；\n!?！？]+/);

    sentences.forEach(function (sentence) {
      if (!sentence || !sentence.trim()) return;
      // v0.1.6.16：无具体日期「X月X日」的句子（通知标题、发文字号、引导句、鼓励语、结尾客套）直接忽略，不报未识别
      if (!/(\d{1,2})月(\d{1,2})日/.test(sentence)) return;
      var produced = false;

      // —— 1) 放假区间：m月d日(可选星期/农历括注)至(m月)?d日；后半月份可省略，表示同月 ——
      var rangeRe = /(\d{1,2})月(\d{1,2})日\s*(?:[（(][^）)]*[)）])?\s*(?:至|到|—|~)\s*(?:(\d{1,2})月)?(\d{1,2})日/g;
      var rm;
      while ((rm = rangeRe.exec(sentence)) !== null) {
        if (!/放假/.test(sentence)) continue; // 该句必须含“放假”才认定为放假区间
        var sm = +rm[1], sd = +rm[2], em = rm[3] ? +rm[3] : sm, ed = +rm[4];
        if (!isValidDate(year, sm, sd) || !isValidDate(year, em, ed)) {
          warnings.push('放假日期不合法：' + sentence.trim());
          continue;
        }
        var dates = expand(year, sm, sd, em, ed);
        if (dates.length === 0) {
          warnings.push('放假区间无法展开：' + sentence.trim());
          continue;
        }
        batches.push({
          id: '',
          kind: 'off',
          name: extractName(sentence, rm.index),
          keyDay: fmt(year, sm, sd),
          start: fmt(year, sm, sd),
          end: fmt(year, em, ed),
          dates: dates
        });
        produced = true;
      }

      // —— 2) 单日放假（区间正则未命中时）：m月d日(可选括注)放假 ——
      var singleRe = /(\d{1,2})月(\d{1,2})日\s*(?:[（(][^）)]*[)）])?\s*放假/g;
      var sm2;
      while ((sm2 = singleRe.exec(sentence)) !== null) {
        var m2 = +sm2[1], d2 = +sm2[2];
        if (!isValidDate(year, m2, d2)) { warnings.push('单日放假日期不合法：' + sentence.trim()); continue; }
        batches.push({
          id: '', kind: 'off',
          name: extractName(sentence, sm2.index),
          keyDay: fmt(year, m2, d2),
          start: fmt(year, m2, d2), end: fmt(year, m2, d2),
          dates: [fmt(year, m2, d2)]
        });
        produced = true;
      }

      // —— 3) 调休补班：句内含“上班/补班”，提取全部带星期括注的日期（顿号并列多日，动词在末尾）——
      if (/上班|补班/.test(sentence)) {
        var dayRe = /(\d{1,2})月(\d{1,2})日\s*[（(]\s*(?:星期|周)[一二三四五六日天]\s*[)）]/g;
        var wm;
        while ((wm = dayRe.exec(sentence)) !== null) {
          var mw = +wm[1], dw = +wm[2];
          if (!isValidDate(year, mw, dw)) { warnings.push('补班日期不合法：' + sentence.trim()); continue; }
          batches.push({
            id: '', kind: 'adjust', name: '', keyDay: '',
            start: fmt(year, mw, dw), end: fmt(year, mw, dw),
            dates: [fmt(year, mw, dw)]
          });
          produced = true;
        }
      }

      // 句子含“放假/上班/补班”关键词却一条都没识别出来 → 记告警，便于管理页人工补录
      if (!produced && /放假|上班|补班/.test(sentence)) {
        warnings.push('未识别句式：' + sentence.trim());
      }
    });

    return { batches: batches, warnings: warnings };
  }

  window.PiggyNoticeParser = { parse: parse };
})();
