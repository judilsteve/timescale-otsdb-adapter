namespace TimescaleOpenTsdbAdapter.Aggregators;

public interface IAggregator
{
    public void AddNext(double? value);
    public double? Result { get; }
}

public class MeanAggregator : IAggregator
{
    private int count;
    private double? sum;

    public void AddNext(double? value)
    {
        if(value.HasValue)
        {
            count++;
            if(sum.HasValue) sum += value;
            else sum = value;
        }
    }

    public double? Result => sum / count;
}

public class MedianAggregator : IAggregator
{
    private readonly List<double> values = [];
    private bool sorted = false;

    public void AddNext(double? value)
    {
        if(value.HasValue)
        {
            values.Add(value.Value);
            sorted = false;
        }
    }

    public double? Result {
        get {
            if(!sorted)
            {
                values.Sort();
                sorted = true;
            }

            if(values.Count % 2 == 0)
            {
                return (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
            }
            else
            {
                return values[values.Count / 2];
            }
        }
    }
}

public class CountAggregator : IAggregator
{
    private int count;

    public void AddNext(double? value)
    {
        if(value.HasValue) count++;
    }

    public double? Result => count;
}

public class MinAggregator : IAggregator
{
    private double? min;

    public void AddNext(double? value)
    {
        if (!min.HasValue || value < min) min = value;
    }

    public double? Result => min;
}

public class MaxAggregator : IAggregator
{
    private double? max;

    public void AddNext(double? value)
    {
        if (!max.HasValue || value > max) max = value;
    }

    public double? Result => max;
}

public class SumAggregator : IAggregator
{
    private double? sum;

    public void AddNext(double? value)
    {
        if(value.HasValue)
        {
            if(sum.HasValue) sum += value;
            else sum = value;
        }
    }

    public double? Result => sum;
}

public class FirstAggregator : IAggregator
{
    private double? first;

    public void AddNext(double? value)
    {
        if(!value.HasValue || first.HasValue) return;
        first = value;
    }

    public double? Result => first;
}

public class LastAggregator : IAggregator
{
    private double? last;

    public void AddNext(double? value)
    {
        if(value.HasValue) last = value;
    }

    public double? Result => last;
}
