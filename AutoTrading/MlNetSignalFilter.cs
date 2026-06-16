using System.Threading.Tasks;

namespace MoneyCreator.AutoTrading
{
    public interface IMlNetSignalFilter
    {
        Task<TradeSignal> ReviewAsync(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings);
    }

    public sealed class NullMlNetSignalFilter : IMlNetSignalFilter
    {
        public Task<TradeSignal> ReviewAsync(TradeSignal signal, FeatureSnapshot feature, AutoTradeSettings settings)
        {
            return Task.FromResult(signal);
        }
    }
}
