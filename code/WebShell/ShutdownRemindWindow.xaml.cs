using System;
using System.Windows;
using System.Windows.Threading;
using PiggyCalendar.WebShell.Services;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// 关机/重启前提醒弹窗（v0.1.6.10，替代托盘气泡；v1.0.1 文案随动作动态）。
    /// 置顶、定位到小组件所在屏幕居中、实时倒计时。
    /// 按钮①「确认并关闭」：仅关弹窗，任务照常；按钮②「取消本次自动关机/重启」：跳过本次、保留周期开关（方案A）。
    /// </summary>
    public partial class ShutdownRemindWindow : Window
    {
        private readonly Func<DateTime?> _targetProvider; // v0.1.6.17：弹窗不再持有固定 target，实时读调度服务当前目标
        private readonly Func<string> _actionProvider;    // v1.0.1：实时读动作（shutdown/restart），切换后文案立即跟随
        private readonly DispatcherTimer _timer;
        private readonly Action _onCancelOnce;

        public ShutdownRemindWindow(Func<DateTime?> targetProvider, Func<string> actionProvider, Action onCancelOnce, Action onClosed)
        {
            InitializeComponent();
            _targetProvider = targetProvider;
            _actionProvider = actionProvider;
            _onCancelOnce = onCancelOnce;
            Closed += (s, e) => { _timer?.Stop(); onClosed?.Invoke(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateCountdown();
            UpdateTexts();
            UpdateCountdown();
            _timer.Start();
        }

        /// <summary>当前动作的中文名：restart=重启，其余=关机。</summary>
        private string ActionCn()
        {
            try { return _actionProvider != null && _actionProvider() == "restart" ? "重启" : "关机"; }
            catch { return "关机"; }
        }

        /// <summary>按动作刷新标题 / 说明 / 按钮文案（v1.0.1）。</summary>
        private void UpdateTexts()
        {
            var cn = ActionCn();
            Title = "自动" + cn + "提醒";
            HeadText.Text = "自动" + cn + "提醒";
            DescText.Text = "距自动" + cn + "还有";
            ConfirmBtn.ToolTip = "关闭弹窗，" + cn + "照常执行";
            CancelOnceBtn.Content = "取消本次自动" + cn;
            CancelOnceBtn.ToolTip = "仅跳过本次" + cn + "，到点不" + cn + "，下个周期照常排程";
        }

        private void UpdateCountdown()
        {
            var target = _targetProvider?.Invoke();
            if (target == null) { Close(); return; } // v0.1.6.17：调度目标已取消/关闭 → 弹窗自动关闭
            UpdateTexts(); // v1.0.1：每秒同步动作文案，用户切换关机/重启后立即跟随
            var rem = target.Value - DateTime.Now;
            if (rem <= TimeSpan.Zero)
            {
                CountdownText.Text = "0秒";
                Close(); // 已到任务时刻，弹窗自动关闭
                return;
            }
            CountdownText.Text = (int)rem.TotalMinutes + "分 " + rem.Seconds + "秒";
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Close(); // 确认并关闭：任务照常执行
        }

        private void CancelOnce_Click(object sender, RoutedEventArgs e)
        {
            try { _onCancelOnce?.Invoke(); } catch { }
            Close(); // 取消本次自动任务（仅跳过本次，保留周期开关）
        }
    }
}
