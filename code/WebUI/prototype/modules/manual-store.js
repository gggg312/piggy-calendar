/* ============================================================
 * PiggyManualStore —— 节假日本地数据存储与双分区索引（M0-7 打通；M0-8b 升级 v2 分区）
 * 数据来源：壳层读写 运行组件/manual-holidays.json（GetHolidayData 完整 v2；CRUD 走细粒度桥）。
 * 分区：manual 用户手动维护；remote 在线同步产物（M0-8c 填充）；两者物理隔离，手动操作不触碰 remote。
 * 索引：分别建立 manual / remote 的放假、补班日期集合；status 先查 manual，未命中再查 remote。
 * 对外：window.PiggyManualStore.init() / status(y, month1, day) / isReady()
 *       addBatch / removeBatch / clearManual / reload（M0-10 管理页调用，M0-8b 仅提供后端能力）
 * ============================================================ */
(function () {
  'use strict';

  // manual 分区索引（用户手动维护；管理页仅展示此分区，B 方案已剥离内置样本）
  var manualOff = {}, manualName = {}, manualKeyDay = {}, manualAdjust = {};
  // seed 独立内置分区（B 方案：2026 样本内置、不入 manual、不写盘；日历照常显示）
  var seedOff = {}, seedName = {}, seedKeyDay = {}, seedAdjust = {};
  // remote 分区索引（M0-8c 在线同步填充；v0.1.6.48 起记录 keyDay 正日）
  var remoteOff = {}, remoteName = {}, remoteKeyDay = {}, remoteAdjust = {};
  var ready = false;
  var cached = null; // 完整 v2 结构缓存，供 CRUD 后重建索引
  var syncListenerBound = false; // sync-updated 监听只绑定一次
  var manualListenerBound = false; // manual-updated 监听只绑定一次（D 方案：手动 CRUD 后刷新）

  function pad(n) { return String(n).padStart(2, '0'); }
  function dateKey(y, m, d) { return y + '-' + pad(m) + '-' + pad(d); }

  /** 展开 start~end（yyyy-MM-dd）为日期键数组。 */
  function expandDates(start, end) {
    var out = [];
    var s = new Date(start + 'T00:00:00');
    var e = new Date(end + 'T00:00:00');
    if (isNaN(s.getTime()) || isNaN(e.getTime())) return out;
    var cur = new Date(s);
    while (cur <= e) {
      out.push(dateKey(cur.getFullYear(), cur.getMonth() + 1, cur.getDate()));
      cur.setDate(cur.getDate() + 1);
    }
    return out;
  }

  /** 把一批次索引进指定分区容器；返回该分区集合引用。 */
  function indexBatch(b, offSet, nameMap, adjustSet, keyDayMap) {
    var dates = (b.dates && b.dates.length) ? b.dates : expandDates(b.start, b.end);
    dates.forEach(function (k) {
      if (b.kind === 'adjust') {
        adjustSet[k] = true;
      } else {
        offSet[k] = true;
        if (b.name) nameMap[k] = b.name;
      }
    });
    if (keyDayMap && b.kind !== 'adjust' && (b.keyDay || b.start)) {
      keyDayMap[b.keyDay || b.start] = true;
    }
  }

  /** 清空并重建 manual / seed / remote 三套索引（seed 内置样本每次随建，固定不变）。 */
  function indexData(data) {
    manualOff = {}; manualName = {}; manualKeyDay = {}; manualAdjust = {};
    seedOff = {}; seedName = {}; seedKeyDay = {}; seedAdjust = {};
    remoteOff = {}; remoteName = {}; remoteKeyDay = {}; remoteAdjust = {};

    var manualBatches = (data && data.manual && Array.isArray(data.manual.batches))
      ? data.manual.batches : [];
    manualBatches.forEach(function (b) {
      indexBatch(b, manualOff, manualName, manualAdjust, manualKeyDay);
    });

    // seed：内置样本（2026 + 2027），独立分区（不含用户手动、不写盘、管理页不展示；B 方案）
    // v0.1.6.93：新增 2027 内置数据，首次运行/断网时也能显示 2027 放假与补班
    var samples = [window.PiggySampleHolidays2026, window.PiggySampleHolidays2027];
    samples.forEach(function (sample) {
      if (sample && Array.isArray(sample.batches)) {
        sample.batches.forEach(function (b) {
          indexBatch(b, seedOff, seedName, seedAdjust, seedKeyDay);
        });
      }
    });

    // remote：years[yyyy].batches 逐年份合并索引；M0-8c 填充，当前为空。
    var years = (data && data.remote && data.remote.years) || {};
    Object.keys(years).forEach(function (yyyy) {
      var y = years[yyyy];
      var batches = (y && Array.isArray(y.batches)) ? y.batches : [];
      batches.forEach(function (b) {
        /* v0.1.6.48：远程批次必须记 keyDay（正日），否则 date-model 里
           "m.isKeyDay && m.manualName" 这条路永远走不到，2027 等没有手动/内置样本的年份
           假期正日只显示农历、不显示"元旦/清明节"（用户 2026-11-06 报障）。 */
        indexBatch(b, remoteOff, remoteName, remoteAdjust, remoteKeyDay);
      });
    });
  }

  /** 从壳层拉取完整 v2 结构；无桥或失败返回 null。 */
  async function fetchData() {
    if (window.Bridge && window.Bridge.invoke) {
      try { return JSON.parse(await window.Bridge.invoke('GetHolidayData')); }
      catch (_e) { return null; }
    }
    return null;
  }

  /**
   * 启动初始化：拉取 v2（manual + remote）；内置 2026 样本作为独立 seed 分区（B 方案，
   * 不再播种写盘进 manual）；重建索引。
   */
  async function init() {
    var data = await fetchData();

    if (!data || typeof data !== 'object') data = { version: 2, manual: { batches: [] }, remote: { years: {} } };

    cached = data;
    indexData(data);
    ready = true;

    // M0-8c：壳层同步完成后推送 sync-updated，重建 remote 索引（仅注册一次）。
    if (window.Bridge && window.Bridge.on) {
      if (!syncListenerBound) {
        syncListenerBound = true;
        window.Bridge.on('sync-updated', function () { reload(); });
      }
      // D 方案：手动 CRUD（panel 窗口经壳桥）成功后广播 manual-updated → 此处 reload，日历即时刷新
      if (!manualListenerBound) {
        manualListenerBound = true;
        window.Bridge.on('manual-updated', function () { reload(); });
      }
    }
  }

  /** 重新从壳层拉取并重建索引（CRUD 成功后调用）。 */
  async function reload() {
    var data = await fetchData();
    if (data && typeof data === 'object') {
      cached = data;
      indexData(data);
    }
  }

  /**
   * 查询某日状态；优先级 manual > seed > remote（内置样本 seed 在手动之下、远程之上）。
   * @returns {{isOff:boolean, isAdjustWork:boolean, manualName:string|null, isKeyDay:boolean, source:string|null}}
   */
  function status(year, month1, day) {
    var k = dateKey(year, month1, day);
    if (manualAdjust[k]) return { isOff: false, isAdjustWork: true, manualName: null, isKeyDay: false, source: 'manual' };
    if (manualOff[k]) return {
      isOff: true, isAdjustWork: false,
      manualName: manualName[k] || null, isKeyDay: !!manualKeyDay[k], source: 'manual'
    };
    if (seedAdjust[k]) return { isOff: false, isAdjustWork: true, manualName: null, isKeyDay: false, source: 'seed' };
    if (seedOff[k]) return {
      isOff: true, isAdjustWork: false,
      manualName: seedName[k] || null, isKeyDay: !!seedKeyDay[k], source: 'seed'
    };
    if (remoteAdjust[k]) return { isOff: false, isAdjustWork: true, manualName: null, isKeyDay: false, source: 'remote' };
    if (remoteOff[k]) return {
      isOff: true, isAdjustWork: false,
      manualName: remoteName[k] || null, isKeyDay: !!remoteKeyDay[k], source: 'remote'
    };
    return { isOff: false, isAdjustWork: false, manualName: null, isKeyDay: false, source: null };
  }

  // ---------- 手动 CRUD（细粒度桥；只改 manual，不触碰 remote；M0-10 管理页接入） ----------

  /** 新增/更新一个手动批次；成功后重建索引。返回 {ok, msg}。 */
  async function addBatch(batch) {
    if (!window.Bridge || !window.Bridge.invoke) return { ok: false, msg: '桥不可用' };
    var r = await window.Bridge.invoke('AddManualBatch', JSON.stringify(batch));
    var ok = r === 'ok';
    if (ok) await reload();
    return { ok: ok, msg: r };
  }

  /** 按 id 删除手动批次；成功后重建索引。返回 {ok, msg}。 */
  async function removeBatch(id) {
    if (!window.Bridge || !window.Bridge.invoke) return { ok: false, msg: '桥不可用' };
    var r = await window.Bridge.invoke('RemoveManualBatch', id);
    var ok = r === 'ok';
    if (ok) await reload();
    return { ok: ok, msg: r };
  }

  /** 清空全部手动批次（remote 保留）；成功后重建索引。返回 {ok, msg}。 */
  async function clearManual() {
    if (!window.Bridge || !window.Bridge.invoke) return { ok: false, msg: '桥不可用' };
    var r = await window.Bridge.invoke('ClearManual');
    var ok = r === 'ok';
    if (ok) await reload();
    return { ok: ok, msg: r };
  }

  window.PiggyManualStore = {
    init: init,
    status: status,
    isReady: function () { return ready; },
    addBatch: addBatch,
    removeBatch: removeBatch,
    clearManual: clearManual,
    reload: reload,
    getData: function () { return cached; }
  };
})();
