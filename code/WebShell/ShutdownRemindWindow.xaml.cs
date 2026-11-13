using System;
using System.Windows;
using System.Windows.Threading;
using PiggyCalendar.WebShell.Services;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// 关机前提醒弹窗（v0.1.6.10，替代托盘气泡）。置顶、定位到小组件所在屏幕居中、实时倒计时。
    /// 按钮①「确认并关闭」：仅关弹窗，关机照常；按钮②「取消本次自动关机」：跳过本次、保留周期开关（方案A）。
    /// </summary>
    public partial class ShutdownRemindWindow : Window
    {
        private readonly Func<DateTime?> _targetProvider; // v0.1.6.17：弹窗不再持有固定 target，实时读调度服务当前目标
        private readonly DispatcherTimer _timer;
        private readonly Action _onCancelOnce;

        public ShutdownRemindWindow(Func<DateTime?> targetProvider, Action onCancelOnce, Action onClosed)
        {
            InitializeComponent();
            _targetProvider = targetProvider;
            _onCancelOnce = onCancelOnce;
            Closed += (s, e) => { _timer?.Stop(); onClosed?.Invoke(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateCountdown();
            UpdateCountdown();
            _timer.Start();
        }

        private void UpdateCountdown()
        {
            var target = _targetProvider?.Invoke();
            if (target == null) { Close(); return; } // v0.1.6.17：调度目标已取消/关闭 → 弹窗自动关闭
            var rem = target.Value - DateTime.Now;
            if (rem <= TimeSpan.Zero)
            {
                CountdownText.Text = "0秒";
                Close(); // 已到关机时刻，弹窗自动关闭
                return;
            }
            CountdownText.Text = (int)rem.TotalMinutes + "分 " + rem.Seconds + "秒";
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Close(); // 确认并关闭：关机照常执行
        }

        private void CancelOnce_Click(object sender, RoutedEventArgs e)
        {
            try { _onCancelOnce?.Invoke(); } catch { }
            Close(); // 取消本次自动关机（仅跳过本次，保留周期开关）
        }
    }
}
