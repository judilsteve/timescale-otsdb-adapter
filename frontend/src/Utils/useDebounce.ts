import { useEffect, useState } from "react";

export default function useDebounced<T>(value: T, delayMillis: number) {
    const [debouncedValue, setDebouncedValue] = useState(value);

    useEffect(() => {
        const timeoutHandle = setTimeout(
            () => setDebouncedValue(value),
            delayMillis
        );

        return () => clearTimeout(timeoutHandle);
    }, [value, delayMillis]);

    return debouncedValue;
}

export function useOneWayDebounced<T>(value: T, delayMillis: number) {
    const [debouncedValue, setDebouncedValue] = useState(value);

    useEffect(() => {
        if(value !== undefined) {
            setDebouncedValue(value);
        } else {
            const timeoutHandle = setTimeout(
                () => setDebouncedValue(value),
                delayMillis
            );
            return () => clearTimeout(timeoutHandle);
        }
    }, [value, delayMillis]);

    return debouncedValue;
}

export function useOldValueWhileLoading<T>(value: T, loading: boolean) {
    const [debouncedValue, setDebouncedValue] = useState(value);

    useEffect(() => {
        if(!loading) {
            setDebouncedValue(value);
        }
    }, [value, loading]);

    return debouncedValue;
}
