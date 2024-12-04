import { useCallback, useMemo, useState } from 'react';

import dayjs from 'dayjs';

import { printAbsolute, printDate, printRelative, printTime } from 'src/Utils/dateUtils';

import { Line } from 'react-chartjs-2';
import { Chart, LinearScale, LogarithmicScale, PointElement, LineElement, Tooltip, Legend, TooltipModel, TooltipItem } from 'chart.js'

import { OtsdbResponse } from 'src/Types/OtsdbTypes';

import './CanvasTimeSeriesGraph.less';
import { useSharedState } from 'src/Hooks/useSharedState';
import { graphComponentIdState, graphXBoundsState, graphYBoundsState, useLogScaleState } from 'src/State/sharedState';
import { useOneWayDebounced } from 'src/Utils/useDebounce';

Chart.register(LinearScale, LogarithmicScale, PointElement, LineElement, Tooltip, Legend);

// Colourblind-safe rainbow colour scheme
// Taken from https://personal.sron.nl/~pault/ (fig 21),
// shuffled so that similar colours are not next to each other.
const colours = [
    '#4EB265',
    '#1965B0',
    '#E8601C',
    '#882E72',
    '#DC050C',
    '#7BAFDE',
    '#F7F056',
    '#D1BBD7',
    '#5289C7',
    '#CAE0AB',
    '#AE76A3',
    '#F4A736',
];

const shapes = [
    'circle',
    'rect',
    'rectRot',
    'triangle',
    'rectRounded'
];

export interface CanvasTimeSeriesGraphProps {
    data: OtsdbResponse[];
    groupingTagKeys: Set<string> | undefined;
}

export default function CanvasTimeSeriesGraph(props: CanvasTimeSeriesGraphProps) {
    const { data, groupingTagKeys } = props;

    const [hoveredSeries, setHoveredSeries] = useState<string | undefined>();
    const debouncedhoveredSeries = useOneWayDebounced(hoveredSeries, 200);

    const [tooltipLeft, setTooltipLeft] = useState(0);
    const [tooltipTop, setTooltipTop] = useState(0);
    const [tooltipYTranslate, setTooltipYTranslate] = useState('-100%');
    const [tooltipXTranslate, setTooltipXTranslate] = useState('-100%');
    const [tooltipItem, setTooltipItem] = useState<TooltipItem<"line"> | undefined>();
    const updateTooltip = useCallback((ctx: { chart: Chart<"line">, tooltip: TooltipModel<"line"> }) => {
        const { chart, tooltip } = ctx;

        const { caretX, caretY, dataPoints, opacity }  = tooltip;
        const { offsetLeft, offsetTop, height, width } = chart.canvas;

        const newTooltipItem = opacity ? dataPoints[0] : undefined

        // This will run constantly while the user hovers a data-point if we don't check and bail early
        if(newTooltipItem?.datasetIndex === tooltipItem?.datasetIndex
            && newTooltipItem?.dataIndex === tooltipItem?.dataIndex)
            return;

        setTooltipItem(newTooltipItem);
        setTooltipLeft(offsetLeft + caretX);
        setTooltipTop(offsetTop + caretY);
        setTooltipYTranslate(caretY > height / 2 ? '-100%' : '0');
        setTooltipXTranslate(caretX > width / 2 ? '-100%' : '0');
    }, [tooltipItem]);

    const debouncedTooltipItem = useOneWayDebounced(tooltipItem, 200);

    const tooltipStyle = {
        left: tooltipLeft,
        top: tooltipTop,
        display: debouncedTooltipItem ? undefined : 'none',
        transform: `translate(${tooltipXTranslate}, ${tooltipYTranslate})`
    };

    const tickFormatter = useMemo(() => {
        let earliestPoint = Number.POSITIVE_INFINITY;
        let latestPoint = Number.NEGATIVE_INFINITY;
        for(const series of data) {
            for(const tStr of Object.keys(series.dps)) {
                const t = parseFloat(tStr);
                if(t < earliestPoint) earliestPoint = t;
                if(t > latestPoint) latestPoint = t;
            }
        }
        const spanSeconds = latestPoint - earliestPoint;
        const oneDaySeconds = 24 * 3600;
        if(spanSeconds <= oneDaySeconds) {
            return printTime;
        } else if(spanSeconds >= 7 * oneDaySeconds) {
            return printDate;
        } else {
            return printAbsolute;
        }
    }, [data]);

    const chartDatasets = useMemo(() => data.map((s, i) => {
        const interestingTags = Object.entries(s.tags).filter(([k, ]) => !groupingTagKeys || groupingTagKeys.has(k));
        const seriesName = `${s.metric}{${interestingTags.map((([k, v]) => `${k}=${v}`)).join(',')}}`;

        const colour = colours[i % colours.length] + (!debouncedhoveredSeries || debouncedhoveredSeries === seriesName ? '' : '30');
        return {
            label: seriesName,
            data: Object.entries(s.dps).map(([tStr, v]) => ({
                x: parseFloat(tStr), y:
                v === null || v === "NaN" ? Number.NaN : v as number
            })),
            backgroundColor: colour,
            borderColor: colour,
            pointStyle: shapes[Math.floor(i / colours.length) % shapes.length],
            pointRadius: 5,
            fill: false,
            spanGaps: false,
        };
    }), [data, debouncedhoveredSeries, groupingTagKeys]);

    const TimeSeriesTooltip = useCallback((tooltipItem: TooltipItem<"line">) => {
        return <>
            <h3>{ chartDatasets[tooltipItem.datasetIndex].label }</h3>
            <p>Time: {printAbsolute(dayjs(tooltipItem.parsed.x * 1000))} ({printRelative(dayjs(tooltipItem.parsed.x * 1000))})</p>
            <p>Value: {tooltipItem.parsed.y}</p>
            <h4>Tags:</h4>
            <ul>
                {Object.entries(data[tooltipItem.datasetIndex].tags).map(([k, v]) => <li key={k}>{k}: {v}</li>)}
            </ul>
        </>
    }, [data, chartDatasets]);

    const [[startTs, endTs], ] = useSharedState(graphXBoundsState);
    const [[minY, maxY], ] = useSharedState(graphYBoundsState);
    const [useLogScale,] = useSharedState(useLogScaleState);
    const [graphId,] = useSharedState(graphComponentIdState);

    const textColour = 'rgba(255, 255, 255, 0.87)';
    const gridLineColour = 'rgba(255, 255, 255, 0.4)';

    return <div id="responsive-graph-container">
        <Line
            key={graphId}
            data={{ datasets: chartDatasets }}
            options={{
                animation: false,
                responsive: true,
                resizeDelay: 100,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        type: 'linear',
                        ticks: {
                            callback: ts => tickFormatter(dayjs(ts as number * 1000)),
                            color: textColour,
                        },
                        grid: { color: gridLineColour, },
                        min: startTs,
                        max: endTs,
                    },
                    y: {
                        ticks: { color: textColour },
                        grid: { color: gridLineColour },
                        suggestedMin: 0,
                        suggestedMax: 0,
                        min: minY,
                        max: maxY,
                        type: useLogScale ? 'logarithmic' : 'linear'
                    }
                },
                elements: {
                    line: {
                        cubicInterpolationMode: 'monotone',
                        spanGaps: false,
                    },
                },
                plugins: {
                    legend: {
                        position: 'bottom',
                        onHover: (_, i) => setHoveredSeries(i.text),
                        onLeave: () => setHoveredSeries(undefined),
                        labels: {
                            usePointStyle: true,
                            color: textColour
                        },
                    },
                    tooltip: {
                        enabled: false,
                        external: updateTooltip
                    },
                },
                onHover: (_, elements) => setHoveredSeries(elements.length ? chartDatasets[elements[0].datasetIndex].label : undefined)
            }}
        />
        <div id='time-series-graph-tooltip-container' style={tooltipStyle}>
            <div id='time-series-graph-tooltip'>
                {debouncedTooltipItem && <TimeSeriesTooltip {...debouncedTooltipItem} />}
            </div>
        </div>
    </div>;
}
