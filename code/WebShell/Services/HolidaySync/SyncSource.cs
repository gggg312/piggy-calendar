using System.Collections.Generic;

namespace PiggyCalendar.WebShell.Services.HolidaySync
{
    /// <summary>
    /// 单个在线节假日源（M0-8c）。urlTemplate 中 {year} 在请求时替换为公历年。
    /// type / format 必须对应客户端已实现的适配器；未实现类型在线清单也不得新增解析格式。
    /// </summary>
    public class SyncSource
    {
        public string Id = "";
        public string Type = "";
        public int Priority = 0;
        public bool Enabled = true;
        public string UrlTemplate = "";
        public string Format = "";
    }

    /// <summary>动态源清单（在线 sources.json 结构；拉取失败/损坏/为空时回退内置兜底清单）。</summary>
    public class SyncManifest
    {
        public int Version = 1;
        public string UpdatedAt = "";
        public List<SyncSource> Sources = new List<SyncSource>();
    }

    /// <summary>
    /// 内置出厂兜底清单（v0.1.6.15 起，不再自建仓库同步）：只保留第三方公开源，全部含「休+班+调休」。
    /// 主源 chinese-days（jsDelivr npm）→ 备选1 chinese-days（unpkg，同数据另一 CDN）→ 备选2 timor.tech（独立源）。
    /// 按 Priority 顺序尝试即停；全部失败保留旧缓存。自维护数据随软件发版更新，不在线自建仓库。
    /// </summary>
    internal static class BuiltInManifest
    {
        public const string ChineseDaysJsdelivrTemplate =
            "https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.json";
        public const string ChineseDaysUnpkgTemplate =
            "https://unpkg.com/chinese-days/dist/years/{year}.json";
        public const string TimorTemplate =
            "https://timor.tech/api/holiday/year/{year}";

        public static SyncManifest Create()
        {
            return new SyncManifest
            {
                Version = 2,
                UpdatedAt = "2026-11-03T00:00:00+08:00",
                Sources = new List<SyncSource>
                {
                    new SyncSource
                    {
                        Id = "chinese-days-jsdelivr", Type = "chinese-days-yearly", Priority = 1, Enabled = true,
                        UrlTemplate = ChineseDaysJsdelivrTemplate, Format = "chinese-days-v1"
                    },
                    new SyncSource
                    {
                        Id = "chinese-days-unpkg", Type = "chinese-days-yearly", Priority = 2, Enabled = true,
                        UrlTemplate = ChineseDaysUnpkgTemplate, Format = "chinese-days-v1"
                    },
                    new SyncSource
                    {
                        Id = "timor", Type = "timor-yearly", Priority = 3, Enabled = true,
                        UrlTemplate = TimorTemplate, Format = "timor-v1"
                    }
                }
            };
        }
    }
}
