
export interface OtsdbQueryPart {
    aggregator: string;
    metric: string;
    rate: boolean;
    rateOptions?: { counter?: boolean; dropResets?: boolean; };
    downsample: string | null;
    tags: { [tagk: string]: string };
    explicitTags: boolean;
}

export interface OtsdbQuery {
    start: string | number;
    end: string | number | null;
    queries: OtsdbQueryPart[];
}

export interface OtsdbResponse {
    metric: string;
    tags: { [tagk: string]: string };
    dps: { [ts: number]: number | null | string };
}
