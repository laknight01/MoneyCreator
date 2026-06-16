using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyCreator.Converters
{
    public class PriceToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double rate)
            {
                if (rate > 0) return Brushes.Red;      // 상승은 빨강
                if (rate < 0) return Brushes.DeepSkyBlue; // 하락은 파랑 (다크모드엔 이게 잘 보임)
            }
            return Brushes.Gray; // 보합은 회색
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class StrengthToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double strength)
            {
                // 체결강도 기준은 100!
                if (strength > 100.0)
                    return Brushes.Red;    // 매수 우위
                if (strength < 100.0)
                    return Brushes.DodgerBlue; // 매도 우위 (깔끔한 파란색)
            }

            return Brushes.Gray; // 100이거나 데이터 없을 때
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
