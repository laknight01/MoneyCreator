using MoneyCreator.Models;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;

namespace MoneyCreator.View
{
    public partial class OrderView : UserControl
    {
        // 결과를 담아 리턴할 구조체
        public class OrderModifyResult
        {
            public bool IsConfirmed { get; set; }
            public int NewPrice { get; set; }
            public int NewQty { get; set; }
        }

        private TaskCompletionSource<OrderModifyResult> _tcs;

        public OrderView()
        {
            InitializeComponent();

            // 숫자만 입력받도록 이벤트 연결 (필요시 XAML에서도 PreviewTextInput="NumberOnly_PreviewTextInput" 추가)
            EditPrice.PreviewTextInput += NumberOnly_PreviewTextInput;
            EditQty.PreviewTextInput += NumberOnly_PreviewTextInput;

            // 붙여넣기 방지
            DataObject.AddPastingHandler(EditPrice, OnPasting);
            DataObject.AddPastingHandler(EditQty, OnPasting);
        }

        /// <summary>
        /// 정정 팝업을 띄우고 결과를 기다리는 정적 메서드
        /// </summary>
        public static Task<OrderModifyResult> ShowModify(Window owner, OrderInfo order)
        {
            var tcs = new TaskCompletionSource<OrderModifyResult>();
            var view = new OrderView();
            view._tcs = tcs;

            // 데이터 주입
            view.TxtName.Text = order.Name;
            view.TxtOrderNo.Text = order.OrderNo;
            view.TxtType.Text = order.OrderType;
            view.TxtStatus.Text = order.Status;
            view.TxtOrigPrice.Text = ((int)order.Price).ToString("N0");
            view.TxtOrigQty.Text = order.Qty.ToString("N0");
            view.TxtExecQty.Text = order.ExecQty.ToString("N0");
            view.TxtUntradedQty.Text = order.UntradedQty.ToString("N0");
            view.EditPrice.Text = ((int)order.Price).ToString();
            view.EditQty.Text = order.UntradedQty.ToString();

            // 부모 창의 'MainGrid'를 찾아서 전체 화면 모달로 띄움
            var mainGrid = owner.FindName("MainGrid") as Grid;
            if (mainGrid != null)
            {
                // 최상위 레이어에 추가 (다른 버튼 클릭 방지)
                mainGrid.Children.Add(view);

                // Grid 내의 모든 Row와 Column을 덮도록 설정
                if (mainGrid.RowDefinitions.Count > 0) Grid.SetRowSpan(view, mainGrid.RowDefinitions.Count);
                if (mainGrid.ColumnDefinitions.Count > 0) Grid.SetColumnSpan(view, mainGrid.ColumnDefinitions.Count);
            }

            view.EditPrice.Focus();
            view.EditPrice.SelectAll();

            return tcs.Task;
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            int.TryParse(EditPrice.Text, out int price);
            int.TryParse(EditQty.Text, out int qty);

            ClosePopup(new OrderModifyResult
            {
                IsConfirmed = true,
                NewPrice = price,
                NewQty = qty
            });
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ClosePopup(new OrderModifyResult { IsConfirmed = false });
        }

        private void ClosePopup(OrderModifyResult result)
        {
            var parent = this.Parent as Grid;
            parent?.Children.Remove(this);
            _tcs?.SetResult(result);
        }

        // --- 숫자 입력 유효성 검사 로직 ---
        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = new Regex("[^0-9]+").IsMatch(e.Text);
        }

        private void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (new Regex("[^0-9]+").IsMatch(text)) e.CancelCommand();
            }
            else e.CancelCommand();
        }
    }
}