namespace TimescaleOpenTsdbAdapter.Utils;

// See http://opentsdb.net/docs/build/html/user_guide/query/index.html#rate
// and http://opentsdb.net/docs/build/html/api_http/query/index.html#rate-options
public interface IRateConverter
{
    public void Reset();
    public abstract bool TryCalcRateChange(DateTimeOffset timestamp, double? value, out double? rateChange);
}

public static class RateConverterExtensions
{
    public static IEnumerable<(long timestamp, double? value)> CalcRateChanges<T>(
        this IRateConverter converter,
        IEnumerable<T> source,
        Func<T, DateTimeOffset> timestampSelector,
        Func<T, double?> valueSelector
    ) {
        foreach(var element in source)
        {
            var timestamp = timestampSelector(element);
            var value = valueSelector(element);

            if(converter.TryCalcRateChange(timestamp, value, out var rateChange))
            {
                yield return (timestamp.ToUnixTimeSeconds(), rateChange);
            }
        }
    }
}

public class PlainRateConverter(DateTimeOffset queryStart) : IRateConverter
{
    private readonly DateTimeOffset queryStart = queryStart;
    private DateTimeOffset lastTimestamp = default;
    private double? lastValue = null;

    public void Reset()
    {
        lastValue = null;
    }

    public bool TryCalcRateChange(DateTimeOffset timestamp, double? value, out double? rateChange)
    {
        rateChange = null;
        if(!value.HasValue) return false;

        var emitPoint = false;
        if(lastValue.HasValue && timestamp >= queryStart)
        {
            rateChange = (value.Value - lastValue.Value) / (timestamp - lastTimestamp).TotalSeconds;
            emitPoint = true;
        }

        lastValue = value;
        lastTimestamp = timestamp;
        return emitPoint;
    }
}

public class CountRateConverter(
    DateTimeOffset queryStart,
    double counterMax,
    bool dropResets)
    : IRateConverter
{
    private readonly DateTimeOffset queryStart = queryStart;
    private readonly double counterMax = counterMax;
    private readonly bool dropResets = dropResets;

    private DateTimeOffset lastTimestamp = default;
    private double? lastValue = null;

    public void Reset()
    {
        lastValue = null;
    }

    public bool TryCalcRateChange(DateTimeOffset timestamp, double? value, out double? rateChange)
    {
        rateChange = null;
        if (!value.HasValue) return false;

        var emitPoint = false;
        if (lastValue.HasValue && timestamp >= queryStart)
        {
            double valueChange;
            if (lastValue > value.Value)
            {
                // Counter has rolled over
                valueChange = (counterMax - lastValue.Value) + value.Value;
                emitPoint = !dropResets;
            }
            else
            {
                valueChange = value.Value - lastValue.Value;
                emitPoint = true;
            }
            rateChange = valueChange / (timestamp - lastTimestamp).TotalSeconds;
        }

        lastValue = value;
        lastTimestamp = timestamp;
        return emitPoint;
    }
}
