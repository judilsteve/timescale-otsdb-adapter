using System.Runtime.CompilerServices;
using TimescaleOpenTsdbAdapter.Utils;

namespace TimescaleOpenTsdbAdapter.Tests;

public class TestAsyncEnumerableExtensions
{
    private static async IAsyncEnumerable<T> SimulateAsyncEnumerable<T>(
        IEnumerable<T> sequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        foreach(var element in sequence)
        {
            yield return element;
            await Task.Delay(0, cancellationToken);
        }
    }

    [Fact]
    public static async Task TestSegmentBy()
    {
        int[] input = [
            ..Enumerable.Range(0, 10),
            ..Enumerable.Range(3, 6),
        ];

        var output = SimulateAsyncEnumerable(input)
            .SegmentBy(i => i / 3);

        (int, int[])[] expected = [
            (0, [0, 1, 2]),
            (1, [3, 4, 5]),
            (2, [6, 7, 8]),
            (3, [9]),
            (1, [3, 4, 5]),
            (2, [6, 7, 8]),
        ];

        var i = 0;
        await foreach(var (key, segment) in output)
        {
            var (expectedKey, expectedSegment) = expected[i++];
            Assert.Equal(expectedKey, key);
            Assert.Equal(expectedSegment, segment);
        }
        Assert.Equal(expected.Length, i);
    }

    [Fact]
    public static async Task TestSegmentByCancellation()
    {
        int[] input = [
            ..Enumerable.Range(0, 10),
        ];

        var output = SimulateAsyncEnumerable(input)
            .SegmentBy(i => i / 3);

        var cancelAfter = 7;
        var cts = new CancellationTokenSource();
        var i = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(async () => {
            await foreach(var (key, segment) in output.WithCancellation(cts.Token))
            {
                await foreach(var item in segment.WithCancellation(cts.Token))
                {
                    if(++i == cancelAfter) cts.Cancel();
                }
            }
        });

        Assert.Equal(cancelAfter, i);
    }
}
