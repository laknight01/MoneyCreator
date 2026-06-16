using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.AutoTrading
{
    public enum TiRexDirection
    {
        Unknown = 0,
        Down = 1,
        Flat = 2,
        Up = 3
    }

    public sealed class TiRexPredictionResult
    {
        public TiRexDirection Direction { get; set; }
        public double PredictedReturn { get; set; }
        public double Confidence { get; set; }
        public string Message { get; set; }

        public static TiRexPredictionResult Unknown(string message)
        {
            return new TiRexPredictionResult
            {
                Direction = TiRexDirection.Unknown,
                PredictedReturn = 0,
                Confidence = 0,
                Message = message
            };
        }
    }
}
