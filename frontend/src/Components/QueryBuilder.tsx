import 'semantic/elements/button.less';
import 'semantic/elements/icon.less';
import 'semantic/elements/loader.less';
import 'semantic/collections/form.less';
import 'semantic/modules/checkbox.less';
import 'semantic/modules/dropdown.less';

import './QueryBuilder.less';

import {
    Form, FormField, FormDropdown,
    Button, ButtonGroup,
    Checkbox,
    FormGroup,
    Icon,
} from 'semantic-ui-react';

import { v4 as uuidv4 } from 'uuid';

import { ChangeEvent, useCallback, useEffect, useState } from 'react';

import { OtsdbQuery } from 'src/Types/OtsdbTypes';
import useSWR from 'swr';
import { swrFetcher } from 'src/Utils/fetchUtils';
import useDebounced, { useOldValueWhileLoading } from 'src/Utils/useDebounce';
import copyToClipboard from 'src/Utils/clipboardUtils';
import dayjs from 'dayjs';
import { toast } from 'src/State/sharedState';
import { printForDateTimeLocalInput } from 'src/Utils/dateUtils';

enum TimePickerMode {
    Relative,
    Absolute,
    Now
}

interface MultiModeTimePickerProps {
    label: string;
    mode: TimePickerMode;
    setMode: (newValue: TimePickerMode) => void;
    value: string | undefined;
    setValue: (newValue: string | undefined) => void;
    allowNow?: boolean;
}

interface TimePickerProps {
    value: string | undefined;
    setValue: (newValue: string | undefined) => void;
}

function RelativeTimePicker(props: TimePickerProps) {
    const { value, setValue } = props;

    const handleChange = useCallback((e: ChangeEvent<HTMLInputElement>) => {
        setValue(`${e.target.value}-ago`);
    }, [setValue]);

    return <FormField inline style={{ display: 'flex', alignItems: 'baseline' }}>
        <input style={{ flexGrow: 1 }} value={value?.replace('-ago', '')} onChange={handleChange} />
        <label>ago</label>
    </FormField>;
}

function AbsoluteTimePicker(props: TimePickerProps) {
    const { value, setValue } = props;

    const handleChange = useCallback((e: ChangeEvent<HTMLInputElement>) => {
        setValue(e.target.value);
    }, [setValue]);

    return <FormField>
        <input style={{ width: '100%' }} type="datetime-local" value={value} onChange={handleChange} />
    </FormField>;
}

function MultiModeTimePicker(props: MultiModeTimePickerProps) {
    const { label, value, setValue, mode, setMode, allowNow } = props;

    const handleSetMode = (newMode: TimePickerMode) => {
        setMode(newMode);
        if(newMode == TimePickerMode.Absolute) setValue(undefined);
        else if(newMode == TimePickerMode.Relative) setValue('1h-ago');
        else setValue('now');
    };

    return <>
        <FormField>
            <label>{label}</label>
            <ButtonGroup fluid>
                {
                    allowNow &&
                    <Button secondary inverted active={mode === TimePickerMode.Now} onClick={() => handleSetMode(TimePickerMode.Now)}>Now</Button>
                }
                <Button secondary inverted active={mode === TimePickerMode.Relative} onClick={() => handleSetMode(TimePickerMode.Relative)}>Relative</Button>
                <Button secondary inverted active={mode === TimePickerMode.Absolute} onClick={() => handleSetMode(TimePickerMode.Absolute)}>Absolute</Button>
            </ButtonGroup>
        </FormField>
        <FormField width={16}>
            {
                mode === TimePickerMode.Relative ? <RelativeTimePicker value={value} setValue={setValue} />
                : mode === TimePickerMode.Absolute ? <AbsoluteTimePicker value={value} setValue={setValue} />
                : <></>
            }
        </FormField>
    </>;
}

const aggregators = [
    'none',
    'avg',
    'median',
    'sum',
    'count',
    'first',
    'last',
    'min',
    'max',
];

const fillPolicies = [
    'none',
    'nan',
    'zero',
    'null'
]

function toOptions(values: string[]) {
    return values.map(x => ({ text: x, value: x }));
}

const aggregatorOptions = toOptions(aggregators);
const downsampleAggregatorOptions = toOptions(aggregators.filter(a => a !== 'none'));
const fillPolicyOptions = toOptions(fillPolicies);

interface DownsamplePickerProps {
    value: string | null;
    setValue: (newValue: string | null) => void;
}

function DownsamplePicker(props: DownsamplePickerProps) {
    const { value, setValue } = props;

    const [interval, setDownsampleInterval] = useState('');
    const [aggregator, setAggregator] = useState<string | undefined>(undefined);
    const [fillPolicy, setFillPolicy] = useState('none');

    useEffect(() => {
        const downsampleComponents = value?.split('-');
        if(downsampleComponents?.length !== 3) {
            setDownsampleInterval('');
            setAggregator(undefined);
            setFillPolicy('none');
        } else {
            setDownsampleInterval(downsampleComponents[0]);
            setAggregator(downsampleComponents[1]);
            setFillPolicy(downsampleComponents[2]);
        }
    }, [value]);

    const updateValue = (interval: string, aggregator: string | undefined, fillPolicy: string) => {
        setValue(interval && aggregator ? `${interval}-${aggregator}-${fillPolicy}` : null);
    };

    return <FormGroup widths="equal">
        <FormField>
            <label>Downsample</label>
            <input
                placeholder="Interval"
                value={interval}
                onChange={e => { setDownsampleInterval(e.target.value); updateValue(e.target.value, aggregator, fillPolicy); }}
                list="downsample-suggestions"
            />
            <datalist id="downsample-suggestions">
                <option value="5s">5s</option>
                <option value="30s">30s</option>
                <option value="1m">1m</option>
                <option value="5m">5m</option>
                <option value="30m">30m</option>
                <option value="1h">1h</option>
                <option value="1d">1d</option>
            </datalist>
        </FormField>
        <FormField>
            <label />
            <FormDropdown
                placeholder='Aggregator'
                selection
                options={downsampleAggregatorOptions}
                value={aggregator}
                onChange={(_, { value }) => { setAggregator(value as string); updateValue(interval, value as string, fillPolicy); }}
            />
        </FormField>
        <FormField>
            <label />
            <FormDropdown
                placeholder='Fill Policy'
                selection
                options={fillPolicyOptions}
                value={fillPolicy}
                onChange={(_, { value }) => { setFillPolicy(value as string); updateValue(interval, aggregator, value as string); }}
            />
        </FormField>
    </FormGroup>
}

interface TagPairProps {
    id: string;
    first?: boolean;
    metric: string;
    tagKey: string;
    setTagKey: (newValue: string) => void;
    tagValue: string;
    setTagValue: (newValue: string) => void;
    canDelete: boolean;
    onDelete: () => void;
    onBlur: () => void;
}

function TagPair(props: TagPairProps) {
    const { first, id, metric, tagKey, setTagKey, tagValue, setTagValue, canDelete, onDelete, onBlur } = props;

    const debouncedTagKey = useDebounced(tagKey, 100);
    const { data: tagKeySuggestions, isLoading: tagKeySuggestionsLoading } = useSWR<string[]>(
        metric && debouncedTagKey ? `/api/suggest/tagKeys/${metric}?q=${debouncedTagKey}&max=25`
        : debouncedTagKey ? `/api/suggest?type=tagk&q=${debouncedTagKey}&max=25`
        : undefined,
        swrFetcher
    );
    const debouncedTagKeySuggestions = useOldValueWhileLoading(tagKeySuggestions, tagKeySuggestionsLoading);

    const debouncedTagValue = useDebounced(tagValue, 100);
    const { data: tagValueSuggestions, isLoading: tagValueSuggestionsLoading } = useSWR<string[]>(
        debouncedTagKey && debouncedTagValue ? `/api/suggest/tagValues/${debouncedTagKey}?q=${debouncedTagValue}&max=25`
        : debouncedTagValue ? `/api/suggest?type=tagv&q=${debouncedTagValue}&max=25`
        : debouncedTagKey ? `/api/suggest/tagValues/${debouncedTagKey}?max=25`
        : undefined,
        swrFetcher
    );
    const debouncedTagValueSuggestions = useOldValueWhileLoading(tagValueSuggestions, tagValueSuggestionsLoading);

    const keyListId = `tag-keys-${id}`;
    const valueListId = `tag-values-${id}`;

    return <FormGroup>
        <FormField style={{ flexGrow: 1}}>
            { first && <label>Tags</label> }
            <input
                style={{ flexGrow: 1 }}
                placeholder="Key"
                value={tagKey}
                onChange={e => setTagKey(e.target.value)}
                list={keyListId}
                onBlur={onBlur}
            />
            <datalist id={keyListId}>
                {debouncedTagKeySuggestions?.map(k => <option key={k} value={k}>{k}</option>)}
            </datalist>
        </FormField>
        <FormField style={{ flexGrow: 1}}>
            { first && <label /> }
            <input
                style={{ flexGrow: 1 }}
                placeholder="Value"
                value={tagValue}
                onChange={e => setTagValue(e.target.value)}
                list={valueListId}
                onBlur={onBlur}
            />
            <datalist id={valueListId}>
                {debouncedTagValueSuggestions?.map(v => <option key={v} value={v}>{v}</option>)}
            </datalist>
        </FormField>
        <FormField>
            { first && <label /> }
            <ButtonGroup className="formButtons">
                <Button secondary icon="delete" disabled={!canDelete} onClick={onDelete} />
            </ButtonGroup>
        </FormField>
    </FormGroup>
}

class Tag {
    id = uuidv4();

    constructor(
        public key = '',
        public value = ''
    ) {}
}

export default function QueryBuilder(props: { runQuery: (q: OtsdbQuery) => Promise<void> | void, loading: boolean }) {
    const { runQuery, loading } = props;

    const [fromMode, setFromMode] = useState<TimePickerMode>(TimePickerMode.Relative);
    const [from, setFrom] = useState<string | undefined>('1h-ago');

    const [toMode, setToMode] = useState<TimePickerMode>(TimePickerMode.Now);
    const [to, setTo] = useState<string | undefined>('now');

    const [metric, setMetric] = useState('');
    const debouncedMetric = useDebounced(metric, 100);
    const { data: metricSuggestions, isLoading: metricSuggestionsLoading } = useSWR<string[]>(
        debouncedMetric ? `/api/suggest?type=metrics&q=${debouncedMetric}&max=25` : undefined,
        swrFetcher
    );
    const debouncedMetricSuggestions = useOldValueWhileLoading(metricSuggestions, metricSuggestionsLoading);

    const [tags, setTags] = useState<Tag[]>([new Tag()]);
    const setTagKey = useCallback((tag: Tag, newKey: string) => {
        tag.key = newKey;
        setTags(tags => [...tags]);
    }, []);
    const setTagValue = useCallback((tag: Tag, newValue: string) => {
        tag.value = newValue;
        setTags(tags => [...tags]);
    }, []);
    const deleteTag = useCallback((id: string) => {
        setTags(tags => tags.filter(t => t.id !== id))
    }, []);

    const pruneTags = useCallback(() => {
        setTags(tags => [...tags.filter((t) => t.key || t.value), new Tag()]);
    }, []);

    const [aggregator, setAggregator] = useState('avg');

    const [downsample, setDownsample] = useState<string | null>(null);

    const [isRate, setIsRate] = useState(false);
    const [isCounter, setIsCounter] = useState(false);
    const [dropResets, setDropResets] = useState(false);

    const valid = from && to && metric;

    const makeQuery = () => ({
        start: fromMode === TimePickerMode.Absolute ? dayjs(from).utc().toISOString() : from!,
        end: toMode === TimePickerMode.Absolute ? dayjs(to).utc().toISOString() : to!,
        queries: [{
            metric,
            downsample,
            tags: tags
                .filter(t => t.key && t.value)
                .reduce((tags, t) => { tags[t.key] = t.value; return tags; }, {} as {[k: string] : string}),
            aggregator,
            rate: isRate,
            rateOptions: {
                counter: isCounter,
                dropResets: dropResets,
            },
            explicitTags: false,
        }]
    });

    const submit = () => void runQuery(makeQuery());

    const [writingToClipboard, setWritingToClipboard] = useState(false);
    const copyQuery = async () => {
        setWritingToClipboard(true);
        await copyToClipboard(makeQuery());
        setWritingToClipboard(false);
    };

    const [importingQuery, setImportingQuery] = useState(false);
    const importQuery = async () => {
        setImportingQuery(true)
        try {
            const text = await navigator.clipboard.readText();
            const query = JSON.parse(text) as OtsdbQuery;

            let fromMode;
            if(typeof(query.start) === 'number') {
                fromMode = TimePickerMode.Absolute;
            } else if(query.start.endsWith('-ago')) {
                fromMode = TimePickerMode.Relative;
            } else {
                fromMode = TimePickerMode.Absolute;
            }
            setFromMode(fromMode);

            if(fromMode === TimePickerMode.Relative) {
                setFrom(query.start as string);
            } else {
                setFrom(printForDateTimeLocalInput(dayjs(typeof(query.start) === 'number' ? query.start * 1000 : query.start)));
            }

            let toMode;
            if(typeof(query.end) === 'number') {
                toMode = TimePickerMode.Absolute;
            } else if (query.end === null || query.end === 'now'){
                toMode = TimePickerMode.Now;
            } else if(query.end.endsWith('-ago')) {
                toMode = TimePickerMode.Relative;
            } else {
                toMode = TimePickerMode.Absolute;
            }
            setToMode(toMode);

            if(toMode === TimePickerMode.Now) {
                setTo('now');
            } else if(toMode === TimePickerMode.Relative) {
                setTo(query.end as string);
            } else {
                setTo(printForDateTimeLocalInput(dayjs(typeof(query.end) === 'number' ? query.end * 1000 : query.end)));
            }

            const firstQuery = query.queries[0];
            setMetric(firstQuery.metric);
            setTags([
                ...Object.entries(firstQuery.tags).map(([k, v]) => new Tag(k, v)),
                new Tag()
            ]);

            setAggregator(firstQuery.aggregator ?? 'none');

            setDownsample(firstQuery.downsample);

            setIsRate(firstQuery.rate ?? false);
            setIsCounter(firstQuery.rateOptions?.counter ?? false);
            setDropResets(firstQuery.rateOptions?.dropResets ?? false);
        } catch(e) {
            console.error('Failed to import query from clipboard', e);
            toast({
                type: 'error',
                title: 'Failed to import query from clipboard',
                description: 'Check browser permissions and clipboard contents.'
            });
        } finally {
            setImportingQuery(false);
        }
    }

    return <Form>
        <FormField>
            <Button loading={importingQuery} disabled={importingQuery} onClick={() => void importQuery()}>
                <Icon name='paste' />Paste JSON
            </Button>
        </FormField>
        <MultiModeTimePicker label="From" value={from} setValue={setFrom} mode={fromMode} setMode={setFromMode} />
        <MultiModeTimePicker label="To" value={to} setValue={setTo} mode={toMode} setMode={setToMode} allowNow />
        <FormField>
            <label>Metric</label>
            <input placeholder="Metric" value={metric} onChange={e => setMetric(e.target.value)} list="metric-suggestions"/>
            <datalist id="metric-suggestions">
                {debouncedMetricSuggestions?.map(m => <option key={m} value={m}>{m}</option>)}
            </datalist>
        </FormField>
        {
            tags.map((t, i) => <TagPair
                first={i == 0}
                id={t.id}
                key={t.id}
                metric={metric}
                tagKey={t.key}
                setTagKey={k => setTagKey(t, k)}
                tagValue={t.value}
                setTagValue={v => setTagValue(t, v)}
                canDelete={i !== tags.length - 1}
                onDelete={() => deleteTag(t.id)}
                onBlur={pruneTags}
            />)
        }
        <FormField>
            <label>Aggregator</label>
            <FormDropdown
                selection
                options={aggregatorOptions}
                value={aggregator}
                onChange={(_, { value }) => setAggregator(value as string)}
            />
        </FormField>
        <DownsamplePicker value={downsample} setValue={setDownsample}/>
        <FormField>
            <Checkbox label="Rate" checked={isRate} onClick={() => setIsRate(r => !r) }/>
            {
                isRate && <>
                    <Checkbox style={{ marginLeft: '1em' }} label="Counter" checked={isCounter} onClick={() => setIsCounter(c => !c)} />
                    { isCounter && <Checkbox style={{ marginLeft: '1em' }} label="Drop Resets" checked={dropResets} onClick={() => setDropResets(dr => !dr)} /> }
                </>
            }
        </FormField>
        <Button
            type='submit'
            primary
            floated='right'
            loading={loading}
            disabled={!valid || loading}
            onClick={submit}>
            <Icon name="play"/>Run Query
        </Button>
        <Button
            floated='right'
            disabled={!valid || writingToClipboard}
            loading={writingToClipboard}
            onClick={() => void copyQuery()}>
            <Icon name="copy"/>Copy JSON
        </Button>
    </Form>;
}
