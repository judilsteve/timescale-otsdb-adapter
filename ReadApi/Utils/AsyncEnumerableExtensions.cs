using System.Runtime.CompilerServices;

namespace TimescaleOpenTsdbAdapter.Utils;

public static class AsyncEnumerableExtensions
{
    private static async IAsyncEnumerable<T> TakeWhile<T>(
        this IAsyncEnumerator<T> enumerator,
        Func<T, bool> predicate,
        Action onComplete)
    {
        while(predicate(enumerator.Current))
        {
            yield return enumerator.Current;
            if(!await enumerator.MoveNextAsync())
            {
                onComplete();
                break;
            }
        }
    }

    public static async IAsyncEnumerable<(TKey, IAsyncEnumerable<T>)> SegmentBy<T, TKey>(
        this IAsyncEnumerable<T> sequence,
        Func<T, TKey> keySelector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        var comparer = EqualityComparer<TKey>.Default;

        await using var enumerator = sequence.GetAsyncEnumerator(cancellationToken);
        if(!await enumerator.MoveNextAsync()) yield break;

        var enumerationComplete = false;
        void OnComplete() { enumerationComplete = true; }
        while(!enumerationComplete)
        {
            var segmentKey = keySelector(enumerator.Current);
            var segment = enumerator.TakeWhile(x => comparer.Equals(segmentKey, keySelector(x)), OnComplete);
            yield return (segmentKey, segment);
        }
    }
}
