// Adapted from https://github.com/judilsteve/VerySimpleFileHost/blob/main/vsfh-client/src/Hooks/useSharedState.ts

import { useCallback, useEffect, useState } from 'react';

export class SharedState<T> {
    private readonly watchers: ((v: T) => void)[] = [];

    constructor(public value: T) {}

    watch(watcher: (v: T) => void) {
        this.watchers.push(watcher);
    }

    removeWatcher(watcher: (v: T) => void) {
        const index = this.watchers.indexOf(watcher);
        this.watchers.splice(index, 1);
    }

    setValue(newValue: T) {
        this.value = newValue;
        for(const watcher of this.watchers) {
            watcher(newValue);
        }
    }

    setValueFunctional(setter: (currentValue: T) => T) {
        const newValue = setter(this.value);
        this.value = newValue;
        for(const watcher of this.watchers) {
            watcher(newValue);
        }
    }
}

export class SharedPersistedState<T> extends SharedState<T> {
    constructor(localStorageKey: string, private initialValue: T, syncAcrossTabs: boolean = true) {
        const persistedJson = window.localStorage.getItem(localStorageKey) ?? null;
        super(persistedJson === null ? initialValue : (JSON.parse(persistedJson) as T));

        // https://developer.mozilla.org/en-US/docs/Web/API/Storage/setItem#exceptions
        this.watch(s => {
            try {
                if(s === null) window.localStorage.removeItem(localStorageKey);
                else window.localStorage.setItem(localStorageKey, JSON.stringify(s));
            } catch(e) {
                console.error('Saving to local storage failed:');
                console.error(e);
            }
        });
        // Handle local storage updates from other tabs. Mozilla states that this event only fires if the update
        // comes from another tab: https://developer.mozilla.org/en-US/docs/Web/API/Window/storage_event
        if(syncAcrossTabs) window.onstorage = e => {
            if(e.storageArea !== window.localStorage || e.key !== localStorageKey) return;
            this.setValue(e.newValue === null ? this.initialValue : (JSON.parse(e.newValue) as T));
        }
    }
}

export function useSharedState<T>(sharedState: SharedState<T>): [T, (newValue: T) => void] {
    const [value, setValue] = useState(sharedState.value);
    useEffect(() => {
        sharedState.watch(setValue);
        return () => sharedState.removeWatcher(setValue);
    }, [sharedState]);
    return [value, useCallback((newValue: T) => sharedState.setValue(newValue), [sharedState])];
}
