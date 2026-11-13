/* ============================================================
 * PiggyLunar —— lunar-javascript 本地封装（M0-6 / v0.1.6.37 扩容）
 * 取：农历日名、白名单节日（农历传统 + 公历 + 按周计算的纪念日）、二十四节气。
 * 不调用：宜忌、方位、八字、生肖、佛/道历、HolidayUtil 法定假。
 * 依赖：../vendor/lunar.min.js（全局 Solar / Lunar）；纯本地计算，无网络请求。
 * 对外：window.PiggyLunar.available() / getDayInfo(year, month0, day)
 * ============================================================ */
(function () {
  'use strict';

  /* v0.1.6.37：三路数据源各一张白名单（库内原名 → 界面短名），不在表内一律过滤。
     农历传统节日（lunar.getFestivals） */
  var FESTIVAL_LUNAR = {
    '春节': '春节',
    '元宵节': '元宵节',
    '端午节': '端午节',
    '七夕节': '七夕节',
    '中秋节': '中秋节',
    '重阳节': '重阳节',
    '腊八节': '腊八节',
    '除夕': '除夕',
    '龙头节': '龙抬头'
  };

  /* 农历杂节（lunar.getOtherFestivals）——用户 2026-11-06 拍板的 C 组 */
  var FESTIVAL_OTHER = {
    '祭灶日': '小年',
    '寒食节': '寒食节',
    '上巳节': '上巳节',
    '中元节': '中元节',
    '寒衣节': '寒衣节',
    '下元节': '下元节'
  };

  /* 公历节日 + 按周计算的纪念日（solar.getFestivals）——A 组的补集 + B 组全部 */
  var FESTIVAL_SOLAR = {
    '情人节': '情人节',
    '妇女节': '妇女节',
    '植树节': '植树节',
    '青年节': '青年节',
    '儿童节': '儿童节',
    '建党节': '建党节',
    '建军节': '建军节',
    '教师节': '教师节',
    '万圣节': '万圣节',
    '平安夜': '平安夜',
    '圣诞节': '圣诞节',
    '母亲节': '母亲节',
    '父亲节': '父亲节',
    '感恩节': '感恩节'
  };

  function available() {
    return typeof window.Solar === 'object' && typeof window.Solar.fromYmd === 'function';
  }

  /* v0.1.6.48：固定公历节日兜底表。
     lunar 的 solar.getFestivals 不含元旦/劳动节/国庆节，这三个名字以前只能靠节假日批次数据（manual/remote）带出来，
     批次没同步到的年份（如 2027）整片日历就没有"元旦""国庆节"（用户 2026-11-06 报障）。 */
  var FIXED_SOLAR_FESTIVAL = {
    '1-1': '元旦',
    '5-1': '劳动节',
    '10-1': '国庆节'
  };

  function pick(list, map, out) {
    if (!list) return false;
    for (var i = 0; i < list.length; i++) {
      var raw = list[i];
      if (Object.prototype.hasOwnProperty.call(map, raw)) {
        out.festivalRaw = raw;
        out.festival = map[raw];
        return true;
      }
    }
    return false;
  }

  /**
   * 查询公历日期对应的农历信息。
   * @param {number} year  公历年
   * @param {number} month0 公历月，0-based（与 JS Date.getMonth() 一致）
   * @param {number} day    公历日
   * @returns {{lunarDay:(string|null), lunarMonth:(string|null),
   *            festival:(string|null), festivalRaw:(string|null),
   *            festivalSource:(string|null), jieqi:(string|null)}}
   *          节日优先级：农历传统 > 农历杂节 > 公历/纪念日（同一天命中多个时只取一个）。
   *          单日异常或库未加载时返回空结构，不向外抛出。
   */
  function getDayInfo(year, month0, day) {
    var empty = { lunarDay: null, lunarMonth: null, festival: null, festivalRaw: null, festivalSource: null, jieqi: null };
    if (!available()) return empty;
    try {
      var solar = window.Solar.fromYmd(year, month0 + 1, day);
      var lunar = solar.getLunar();

      var out = { festival: null, festivalRaw: null, festivalSource: null };
      if (pick(lunar.getFestivals ? lunar.getFestivals() : [], FESTIVAL_LUNAR, out)) {
        out.festivalSource = 'lunar';
      } else if (pick(lunar.getOtherFestivals ? lunar.getOtherFestivals() : [], FESTIVAL_OTHER, out)) {
        out.festivalSource = 'other';
      } else if (pick(solar.getFestivals ? solar.getFestivals() : [], FESTIVAL_SOLAR, out)) {
        out.festivalSource = 'solar';
      }

      /* 二十四节气：非节气日库返回空串 → 归一为 null，前端据此判断是否占位 */
      var jq = (lunar.getJieQi ? lunar.getJieQi() : '') || '';

      /* v0.1.6.48：固定公历节日兜底（元旦 / 劳动节 / 国庆节），与批次数据同名，不影响已有年份 */
      if (!out.festival) {
        var fixedName = FIXED_SOLAR_FESTIVAL[(month0 + 1) + '-' + day];
        if (fixedName) {
          out.festival = fixedName;
          out.festivalRaw = fixedName;
          out.festivalSource = 'fixed';
        }
      }

      /* v0.1.6.48：清明由节气算出来，库里不计入 festival；这里补成「清明节」，与批次口径统一 */
      if (!out.festival && jq === '清明') {
        out.festival = '清明节';
        out.festivalRaw = '清明';
        out.festivalSource = 'jieqi';
      }

      return {
        lunarDay: lunar.getDayInChinese(),
        lunarMonth: lunar.getMonthInChinese(),
        festival: out.festival,
        festivalRaw: out.festivalRaw,
        festivalSource: out.festivalSource,
        jieqi: jq || null
      };
    } catch (err) {
      return empty;
    }
  }

  window.PiggyLunar = {
    available: available,
    getDayInfo: getDayInfo
  };
})();
