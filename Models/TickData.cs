using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.Models
{
    // 데이터 구조체 (키움 API 전달용)
    public class TickData
    {
        public string Code { get; set; }
        public int Price { get; set; }
        public int Volume { get; set; }
        public double Strength { get; set; }
        public long BidVolume { get; set; }
        public long AskVolume { get; set; }
        public string Side { get; set; }//매도 매수 구분
        public DateTime CreatedAt { get; set; }
    }
}
