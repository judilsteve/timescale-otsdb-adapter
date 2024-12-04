import { useCallback, useEffect, useState } from 'react';

import 'semantic/globals/reset.less';
import 'semantic/globals/site.less';

import 'semantic/collections/menu.less';
import 'semantic/elements/icon.less';

import {
    Menu, MenuItem, MenuMenu,
    Icon,
} from 'semantic-ui-react';

import { version, isLocalDev } from 'src/environment.ts';
import ToastContainer from 'src/Components/ToastContainer';
import QueryBuilder from 'src/Components/QueryBuilder';

import 'src/App.less';
import { OtsdbQuery, OtsdbResponse } from './Types/OtsdbTypes';
import { graphXBoundsState, queryResponseState, toast } from './State/sharedState';
import jsonFetch from './Utils/fetchUtils';
import CanvasTimeSeriesGraph from './Components/CanvasTimeSeriesGraph';
import { useSharedState } from './Hooks/useSharedState';
import GraphControls from './Components/GraphControls';

function App() {
    useEffect(() => {
        let pageTitle = 'Timescale Grapher';
        if(isLocalDev) pageTitle += ' (local dev)';
        document.title = pageTitle;
    }, []);

    const [data, setData] = useSharedState(queryResponseState);

    const [groupingTagKeys, setGroupingTagKeys] = useState<Set<string> | undefined>();

    const [, setGraphXBounds] = useSharedState(graphXBoundsState);

    const [loading, setLoading] = useState(false);

    const runQuery = useCallback(async (q: OtsdbQuery) => {
        setLoading(true);
        try {
            const response = await jsonFetch('/api/query', {
                method: 'POST',
                body: JSON.stringify(q)
            });
            if(!response.ok) {
                console.error('Error running query', q, response);
                toast({
                    type: 'error',
                    title: 'Error running query',
                    description: `${response.status} ${response.statusText}. Check the parameters and try again`
                });
                return;
            }

            const newData = await response.json() as OtsdbResponse[];

            if(!newData.length) {
                toast({
                    type: 'warning',
                    title: 'Empty result set',
                    description: 'No data-points were found that matched the query parameters.'
                });
            }

            setData(newData);

            if(q.queries.length !== 1) throw new Error("Series naming for multiple sub-queries not implemented!");
            const query = q.queries[0];
            if(query.aggregator != 'none') {
                // Bit of a kludge, but it'll do
                setGroupingTagKeys(new Set(Object.entries(query.tags).filter(([, v]) =>
                    v.startsWith('regexp(') ||
                    v.startsWith('not_literal_or(') ||
                    v.startsWith('not_iliteral_or') ||
                    v.includes('*') ||
                    v.includes('|')
                ).map(([k, ]) => k)));
            } else {
                setGroupingTagKeys(undefined);
            }

            let earliestPoint = Number.POSITIVE_INFINITY;
            let latestPoint = Number.NEGATIVE_INFINITY;
            for(const series of newData) {
                for(const tStr of Object.keys(series.dps)) {
                    const t = parseFloat(tStr);
                    if(t < earliestPoint) earliestPoint = t;
                    if(t > latestPoint) latestPoint = t;
                }
            }

            // Chart.js will not display anything if the range is zero
            // Adjust manually
            if(earliestPoint === latestPoint) {
                earliestPoint--;
            }

            setGraphXBounds([earliestPoint, latestPoint]);
        } catch(e) {
            console.error('Error running query', q, e);
            toast({
                type: 'error',
                title: 'Error running query',
                description: 'Check the parameters and try again'
            });
        } finally {
            setLoading(false);
        }
    }, [setGraphXBounds, setData]);

    return <>
        <ToastContainer />
        <Menu id="navbar">
            <MenuItem fitted="vertically" header>
                <Icon name="chart area" />
                &nbsp;Timescale Grapher
            </MenuItem>
            <MenuMenu position="right">
                <MenuItem header>v{version}{isLocalDev ? ' (local dev)' : ''}</MenuItem>
            </MenuMenu>
        </Menu>
        <div id="graph-ui">
            <div id="graph-controls">
                <h3>Query</h3>
                <QueryBuilder loading={loading} runQuery={runQuery}/>
                {
                    data && <div style={{ clear: 'both', paddingTop: '1em' }}>
                        <h3>Display Options</h3>
                        <GraphControls />
                    </div>
                }
            </div>
            <div id="graph">
                { data && <CanvasTimeSeriesGraph data={data} groupingTagKeys={groupingTagKeys}/> }
            </div>
        </div>
    </>;
}

export default App;
