using MoneyCreator.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using ScottPlot;
namespace MoneyCreator
{
    /// <summary>
    /// test_chart.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class test_chart : Window
    {
        private test_chartViewModel _vm;

        public test_chart()
        {
            InitializeComponent();

            // 뷰모델 연결
            _vm = new test_chartViewModel();
            this.DataContext = _vm;

            // 차트 그리기
            PlotData();
        }

        private void PlotData()
        {
            // 뷰모델의 데이터를 가져와서 그리기
            MyPlot.Plot.Add.Scatter(_vm.DataX, _vm.DataY);
            MyPlot.Refresh();
        }
    }
}
