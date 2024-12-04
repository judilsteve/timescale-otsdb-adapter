import { SemanticICONS } from "semantic-ui-react";
import { SharedState } from "../Hooks/useSharedState";
import { OtsdbResponse } from "src/Types/OtsdbTypes";

export interface ToastDetails {
    title: string;
    description: string;
    type: 'info' | 'success' | 'warning' | 'error';
    icon?: SemanticICONS;
    lifespanMillis?: number;
}

let nextToastId = 0;

export interface Toast extends ToastDetails {
    id: number;
}

export const toastState = new SharedState<Toast[]>([]);

export const graphXBoundsState = new SharedState<[number | undefined, number | undefined]>([undefined, undefined]);
export const graphYBoundsState = new SharedState<[number | undefined, number | undefined]>([undefined, undefined]);
export const useLogScaleState = new SharedState(false);

export const queryResponseState = new SharedState<OtsdbResponse[] | undefined>(undefined);

export const graphComponentIdState = new SharedState<number>(1);

export function toast(details: ToastDetails) {
    const id = nextToastId++;
    const dismiss = () => {
        toastState.setValue(toastState.value.filter(t => t.id !== id));
    };

    toastState.setValue([ ...toastState.value, { id: id, ...details }]);

    const lifespanMillis = details.lifespanMillis ?? details.type !== 'error' ? 5000 : undefined;
    if(lifespanMillis) {
        window.setTimeout(dismiss, lifespanMillis);
    }

    return dismiss;
}
