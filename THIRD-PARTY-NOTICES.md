# 第三方组件与数据集声明（THIRD-PARTY-NOTICES.md）

> 本项目原创代码为 PolyForm Noncommercial 1.0.0（见 `LICENSE`）。下列第三方组件/数据集保留各自原始许可证；
> 每引入一个新组件，必须在此表登记名称、版本、来源、许可证、署名文本与用途。
> 状态列：`计划引入` = 方案登记、尚未进入工程；`已引入` = 已打包进源码/发布产物。

## 1. 前端框架与组件库

| 名称 | 版本 | 许可证 | 用途 | 状态 | 备注 |
|---|---|---|---|---|---|
| Vue.js | 3.x 全局生产构建 | MIT | 前端视图框架 | 已引入 | 本地 `lib/vue.global.prod.js` 打包，不使用 npm 构建链 |
| Element Plus | v2.7.8（本地打包） | MIT | 前端 UI 组件库 | 已引入 | 本地 `lib/element-plus.min.js` / `element-plus.css` |
| Element Plus Icons | 本地打包版 | MIT | 图标 | 已引入 | 本地 `lib/icons.iife.min.js` |
| localforage | 本地打包版 | MIT | 浏览器端本地键值存储备用 | 已引入 | 本地 `vendor/localforage.min.js`；M0-6 定稿后不再用于在线节假日缓存，手动节假日本地持久化留 M0-8 评估 |

## 2. 历法与节假日数据

| 名称 | 版本 | 许可证 | 用途 | 状态 | 备注 |
|---|---|---|---|---|---|
| lunar-javascript | 1.7.7 | MIT | 公历→农历日名转换、白名单传统节日识别 | 已引入 | 本地 `vendor/lunar.min.js`，纯离线计算，无网络请求；Copyright (c) 2018 6tail，许可全文见 `LICENSES/lunar-javascript-MIT.txt`；仅使用农历日与 `getFestivals()` 传统节日，不调用节气/宜忌/方位/八字/生肖/佛道历与法定假 HolidayUtil |
| chinese-days | 1.5.9 | MIT | 在线兜底源：按年份拉取法定节假日 / 调休补班 JSON，转换为 remote 分区批次 | 已引入 | 不打包 JS 源码，仅经 jsDelivr CDN 按需拉取 `dist/years/{year}.json`；Copyright (c) 2024 Yawei sun，许可全文见 `LICENSES/chinese-days-MIT.txt`；`inLieuDays` 调休替代日按拍板忽略；本地手动条目优先于在线数据 |
| NateScarlet/holiday-cn | — | MIT | （候选）国务院年度法定节假日/调休 JSON | 未采用 | 未引入；在线兜底统一走 chinese-days 分年 JSON（见上一行），本地手动条目优先级高于在线数据 |
| Apple iCloud 日历 ICS 订阅 | 专有服务条款 | （候选）在线日历订阅源 | 未采用 | 闭源服务条款禁止再分发；本项目不接入在线 ICS 订阅，也不做本地 .ics 导入 |
| @qingwu-ui/calendar | — | Apache-2.0 | （候选）日历控件 | 未采用 | M0-5 技术对比后选自研网格，未引入 |
| VibeCalendar（纯前端模块参考） | — | MIT | （候选）日历计算/状态/缓存实现参考 | 未采用 | 仅参考思路，未搬入任何代码 |
| Reicon 图标数据源 | — | 未声明许可证 | UI 图标统一数据源（底部按钮、工具格等） | 已引入 | 数据源 `reicon_icon-data.json`（Outline 权重）；本地仅内联 SVG path 进源码（`fill="currentColor"`），不新增打包文件；README 未声明许可证，仅作 UI 内联参考素材使用 |

## 3. 运行时平台

| 名称 | 许可证 | 用途 | 状态 | 备注 |
|---|---|---|---|---|
| Microsoft Edge WebView2 | 见微软 WebView2 许可 | 内嵌浏览器控件 | 已引入 | NuGet `Microsoft.Web.WebView2`；运行时由终端机自带 Edge WebView2 Runtime 提供 |
| Costura.Fody / Fody | MIT（Costura）/ MIT（Fody） | 单 EXE 打包托管程序集 | 计划引入 | 许可文本见 `LICENSES/` |

## 4. 禁止复用登记（红线，不得进入源码树）

- DeskBox（GPL-3.0-only）：仅可参考布局观感思路，禁止拷入 XAML/C#/样式/图标/素材；
- ElevenClock（GPL-3.0）：仅参考覆盖窗机制思路，禁止拷入代码；
- 优效日历：闭源商业软件，禁止使用其名称、Logo、界面素材、截图。

## 5. 许可证全文存放

第三方许可证全文存放于 `LICENSES/` 目录；引入时在此目录补对应文本，并与上表一一对应。
