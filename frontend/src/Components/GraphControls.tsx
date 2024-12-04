import 'semantic/elements/button.less';
import 'semantic/elements/icon.less';
import 'semantic/collections/form.less';
import 'semantic/modules/checkbox.less';

import {
    Form, FormField,
    Button,
    Checkbox,
    Icon,
    FormGroup,
} from 'semantic-ui-react';

import { useSharedState } from 'src/Hooks/useSharedState';
import { graphComponentIdState, graphXBoundsState, graphYBoundsState, queryResponseState, useLogScaleState } from 'src/State/sharedState';
import { useCallback, useEffect, useState } from 'react';
import { printAbsolute, printForDateTimeLocalInput } from 'src/Utils/dateUtils';
import download from 'src/Utils/downloadUtils';

import dayjs from 'dayjs';
import copyToClipboard from 'src/Utils/clipboardUtils';

export default function GraphControls() {
    const [results, setResults] = useSharedState(queryResponseState);

    const [writingToClipboard, setWritingToClipboard] = useState(false);
    const copyResults = useCallback(async () => {
        setWritingToClipboard(true);
        await copyToClipboard(results);
        setWritingToClipboard(false);
    }, [results]);

    const downloadResults = useCallback(() => {
        const blob = new Blob([JSON.stringify(results, undefined, 4)], { type : 'application/json' });
        download(blob, `Query_${printAbsolute(dayjs())}.json`);
    }, [results]);

    const [graphComponentId, setGraphComponentId] = useSharedState(graphComponentIdState);

    const [useLogScale, setUseLogScale] = useSharedState(useLogScaleState);

    const [xBounds, setXBounds] = useSharedState(graphXBoundsState);

    const [startTime, setStartTime] = useState('');
    const [endTime, setEndTime] = useState('');

    useEffect(() => {
        const [newStart, newEnd] = xBounds;
        setStartTime(newStart === undefined ? '' : printForDateTimeLocalInput(dayjs(newStart * 1000)));
        setEndTime(newEnd === undefined ? '' : printForDateTimeLocalInput(dayjs(newEnd * 1000)));
    }, [xBounds]);

    const updateXBounds = (startTime: string, endTime: string) => {
        const newStartTs = startTime === '' ? undefined : Date.parse(startTime);
        const newEndTs = endTime === '' ? undefined : Date.parse(endTime);
        if(newStartTs !== undefined && isNaN(newStartTs)) return;
        if(newEndTs !== undefined && isNaN(newEndTs)) return;
        if(newStartTs !== undefined && newEndTs !== undefined && newStartTs >= newEndTs) return;
        setXBounds([
            newStartTs === undefined ? undefined : newStartTs / 1000,
            newEndTs === undefined ? undefined : newEndTs / 1000
        ]);
    };

    const [yBounds, setYBounds] = useSharedState(graphYBoundsState);

    const [minY, setMinY] = useState('');
    const [maxY, setMaxY] = useState('');

    useEffect(() => {
        const [newMin, newMax] = yBounds;
        setMinY(newMin === undefined ? '' : newMin.toLocaleString());
        setMaxY(newMax === undefined ? '' : newMax.toLocaleString());
    }, [yBounds]);

    const updateYBounds = (minY: string, maxY: string) => {
        const newMin = minY === '' ? undefined : parseFloat(minY);
        const newMax = maxY === '' ? undefined : parseFloat(maxY);
        if(newMin !== undefined && isNaN(newMin)) return;
        if(newMax !== undefined && isNaN(newMax)) return;
        if(newMin !== undefined && newMax !== undefined && newMin >= newMax) return;
        setYBounds([newMin, newMax]);
    };

    return <Form>
        <FormField>
            <label>Results JSON</label>
                <Button disabled={!results || writingToClipboard} loading={writingToClipboard} onClick={() => void copyResults()}>
                    <Icon name="copy" />Copy
                </Button>
                <Button disabled={!results} onClick={downloadResults}>
                    <Icon name="download" />Download
                </Button>
        </FormField>
        <FormGroup>
            <FormField style={{ flexGrow: 1}}>
                <label>X Axis (Time) Minimum</label>
                <input
                    placeholder="Auto"
                    type='datetime-local'
                    value={startTime}
                    onChange={e => setStartTime(e.target.value)}
                    onBlur={() => updateXBounds(startTime, endTime)}
                />
            </FormField>
            <FormField>
                <label />
                <Button secondary icon="close" onClick={() => { setStartTime(''); updateXBounds('', endTime); }} />
            </FormField>
        </FormGroup>
        <FormGroup>
            <FormField style={{ flexGrow: 1}}>
                <label>X Axis (Time) Maximum</label>
                <input
                    placeholder="Auto"
                    type='datetime-local'
                    value={endTime}
                    onChange={e => setEndTime(e.target.value)}
                    onBlur={() => updateXBounds(startTime, endTime)}
                />
            </FormField>
            <FormField>
                <label />
                <Button secondary icon="close" onClick={() => { setEndTime(''); updateXBounds(startTime, ''); }} />
            </FormField>
        </FormGroup>
        <FormGroup>
            <FormField style={{ flexGrow: 1}}>
                <label>Y Axis Minimum</label>
                <input
                    placeholder="Auto"
                    value={minY}
                    onChange={e => setMinY(e.target.value)}
                    onBlur={() => updateYBounds(minY, maxY)}
                />
            </FormField>
            <FormField>
                <label />
                <Button secondary icon="close" onClick={() => { setMinY(''); updateYBounds('', maxY); }} />
            </FormField>
        </FormGroup>
        <FormGroup>
            <FormField style={{ flexGrow: 1}}>
                <label>Y Axis Maximum</label>
                <input
                    placeholder="Auto"
                    value={maxY}
                    onChange={e => setMaxY(e.target.value)}
                    onBlur={() => updateYBounds(minY, maxY)}
                />
            </FormField>
            <FormField>
                <label />
                <Button secondary icon="close" onClick={() => { setMaxY(''); updateYBounds(minY, ''); }} />
            </FormField>
        </FormGroup>
        <FormField>
            <Checkbox label="Logarithmic Y Scale" checked={useLogScale} onClick={() => setUseLogScale(!useLogScale)} />
        </FormField>
        <Button
            floated='right'
            onClick={() => setResults(undefined)}>
            <Icon name="close" />Clear Graph
        </Button>
        <Button
            floated='right'
            onClick={() => setGraphComponentId(graphComponentId + 1)}>
            <Icon name="refresh" />Force Redraw
        </Button>
    </Form>
}
