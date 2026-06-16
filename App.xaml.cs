using System.Windows;
using System.Windows.Media;
using MoneyCreator.Models;
using MoneyCreator.ViewModels;
using MoneyCreator.Service;

namespace MoneyCreator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 서비스 가져오기 (new 대신 Instance 사용)
            // 이제 어디서든 KiwoomApiService.Instance 하나로 통한다!
            var kiwoomService = KiwoomApiService.Instance;

            // 2. 메인 윈도우 생성
            var window = new MainWindow();

            // [참고] MainWindow 안에서 AxKHOpenAPI 컨트롤을 생성한 후 
            // kiwoomService.SetApiInstance(axControl)를 호출해줘야 해!

            window.Show();
        }
    }
}