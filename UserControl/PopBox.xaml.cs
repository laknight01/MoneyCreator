using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MoneyCreator.View
{
    public enum PopUpType { Confirm, Alert }
    /// <summary>
    /// PopBox.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class PopBox : global::System.Windows.Controls.UserControl
    {
        private TaskCompletionSource<bool> _tcs;

        public PopBox() => InitializeComponent();

        public static Task<bool> Show(Window owner, PopUpType type, string message)
        {
            if (owner == null)
                return Task.FromResult(false);

            var dispatcher = owner.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null ||
                dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished)
                return Task.FromResult(false);

            if (dispatcher.CheckAccess())
                return ShowOnUiThreadAsync(owner, type, message);

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(async () =>
                    {
                        try
                        {
                            bool result = await ShowOnUiThreadAsync(owner, type, message);
                            tcs.TrySetResult(result);
                        }
                        catch
                        {
                            tcs.TrySetResult(false);
                        }
                    }));
            }
            catch
            {
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }

        private static async Task<bool> ShowOnUiThreadAsync(Window owner, PopUpType type, string message)
        {
            if (owner == null)
                return false;

            var container = owner.FindName("MainGrid") as Grid;
            if (container == null)
                return false;

            var popup = new PopBox
            {
                TxtMessage = { Text = message }
            };

            popup.SetupType(type);
            popup._tcs = new TaskCompletionSource<bool>();

            container.Children.Add(popup);
            Grid.SetRowSpan(popup, 99);

            if (type == PopUpType.Alert)
                popup.StartAutoCloseTimer();

            try
            {
                return await popup._tcs.Task;
            }
            finally
            {
                if (container.Children.Contains(popup))
                    container.Children.Remove(popup);
            }
        }

        private void SetupType(PopUpType type)
        {
            if (type == PopUpType.Confirm)
            {
                BtnCancel.Visibility = Visibility.Visible;
                ButtonGrid.Columns = 2; // 취소/확인 반반
                AutoCloseProgress.Visibility = Visibility.Collapsed;
            }
            else // Alert
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                ButtonGrid.Columns = 1; // 확인 버튼 꽉 차게
                AutoCloseProgress.Visibility = Visibility.Visible;
            }
        }

        private async void StartAutoCloseTimer()
        {
            for (int i = 100; i >= 0; i--)
            {
                AutoCloseProgress.Value = i;
                await Task.Delay(30); // 0.1초씩 100번 = 10초
                if (_tcs.Task.IsCompleted) return;
            }
            _tcs.TrySetResult(true); // 시간 다 되면 자동 확인 처리
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => _tcs.TrySetResult(true);
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _tcs.TrySetResult(false);
    }
}
