using System;
using System.Windows;
using System.Windows.Threading;
using PiggyCalendar.WebShell.Services;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// 提醒到点弹窗（v0.1.6.23）。置顶、小组件所在屏幕居中、系统默认提示音（sound 开启时）。
    /// 按钮①「稍后 5 分钟提醒」：推迟 5 分钟再次提醒（单次与周期均生效）；
    /// 按钮②「我知道了」：单次提醒完成即移除；周期提醒当日不再重复弹。
    /// 调度在 ReminderService（独立于小组件窗口），窗口关闭/最小化不影响到点弹窗。
    /// v0.1.6.25：提示音循环——窗口出现时立即响一次，之后每 3 秒重响，直到用户操作关闭窗口。
    /// </summary>
    public partial class ReminderRemindWindow : Window
    {
        private readonly Action _onPostpone;
        private readonly Action _onAcknowledge;
        private readonly Action _onClosed;
        private DispatcherTimer _soundTimer;

        public ReminderRemindWindow(string id, string text, bool sound,
            Action onPostpone, Action onAcknowledge, Action onClosed)
        {
            InitializeComponent();
            _onPostpone = onPostpone;
            _onAcknowledge = onAcknowledge;
            _onClosed = onClosed;
            BodyText.Text = string.IsNullOrWhiteSpace(text) ? "提醒" : text;
            Closed += (s, e) => { StopSoundTimer(); try { _onClosed?.Invoke(); } catch { } };

            // 系统默认提示音：立即响一次 + 每 3 秒循环，直到窗口关闭（用户没看屏幕也能被声音叫来看）
            if (sound)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                _soundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _soundTimer.Tick += (s, e) => { try { System.Media.SystemSounds.Exclamation.Play(); } catch { }; };
                _soundTimer.Start();
            }
        }

        private void StopSoundTimer()
        {
            if (_soundTimer == null) return;
            try { _soundTimer.Stop(); } catch { }
            _soundTimer = null;
        }

        private void Postpone_Click(object sender, RoutedEventArgs e)
        {
            try { _onPostpone?.Invoke(); } catch { }
            Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try { _onAcknowledge?.Invoke(); } catch { }
            Close();
        }
    }
}
