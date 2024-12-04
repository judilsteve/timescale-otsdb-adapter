
create table metric (
    id smallserial primary key not null,
    name varchar(127) not null,
    exists boolean not null default(true), -- This column only exists to support get-or-insert pattern: https://stackoverflow.com/a/57886410
    -- Used to make sure we don't delete tagsets prematurely
    created timestamp with time zone not null default now()
);

-- All of our queries should have a "WHERE metric_name = 'blarg'" clause,
-- for which a hash index is ideal. However, we also want a unique constraint,
-- which is not supported by a hash index, so we also use a traditional btree
-- index.
create unique index metric_name_unique_idx on metric (name);
create index metric_name_hash_idx on metric using hash (name);

create table tagset (
    id serial primary key not null,
    tags jsonb not null,
    exists boolean not null default(true), -- This column only exists to support get-or-insert pattern: https://stackoverflow.com/a/57886410
    -- Used for incremental updates to tagset cache in query service
    -- And also to make sure we don't delete tagsets prematurely
    created timestamp with time zone not null default now()
);
create index tagset_created on tagset (created);

-- A GIN index is ideal for searching for rows with specific key/value pairs.
-- However, we also want a unique constraint, which is not supported by a GIN
-- index, so we also use a traditional btree index.
create unique index tagset_tags_unique_idx on tagset (tags);
create index tagset_tags_gin_idx on tagset using GIN (tags);

create table point (
    -- Be careful about column ordering lest you accidentally introduce padding bytes here
    -- https://www.timescale.com/learn/postgresql-performance-tuning-designing-and-implementing-database-schema
    tagset_id int not null,
    metric_id smallint not null,
    value double precision not null,
    time timestamp with time zone not null
);

-- Foreign key constraints would be nice to have, but they halve our write throughput. We prefer to have the write throughput, because:
-- 1. We really only have one application writing data-points, and we trust it to write data-points with extant metric/tagset IDs
-- 2. We never expect to delete metrics or tagsets (except in the housekeeping job, which has a lot of safeguards)
-- 3. If data-points are somehow written with an invalid metric_id or tagset_id, then as long as we are using inner joins in our
--    queries, these invalid data-points will simply be skipped over and not returned.
--
-- alter table point add constraint fk_point_metric_id foreign key(metric_id) references metric;
-- alter table point add constraint fk_point_tagset_id foreign key(tagset_id) references tagset;

-- Effectively indexes rows by metric_id -> tagset_id -> time, supporting fast aggregation of multiple time series within
-- the same metric over a specific time range. Time bucket partitioning is provided one level above this by the hypertable
-- We definitely want the unique constraint here; as letting duplicate dataopints get into the database would wreak havoc
-- on unaggregated queries or any queries that aggregate by count, sum, average, etc.
create unique index point_metric_id_tagset_id_time_unique_idx on point (metric_id, tagset_id, time);

select create_hypertable(
    'point',
    'time',
    chunk_time_interval => interval '1 hour'
    -- Not worth doing unless we have a distributed cluster
    --,partitioning_column => 'metric_id'
    --,number_partitions => 4
);

-- See https://docs.timescale.com/api/latest/hypertable/add_reorder_policy/
select add_reorder_policy('point', 'point_metric_id_tagset_id_time_unique_idx');

alter table point set (
  timescaledb.compress,
  -- Compress each time series separately. This results in compressed chunks that store data very similarly
  -- to OpenTSDB (1 row = 1 hour of concatenated time/value pairs for a single time series), indexing the
  --compressed data by metric_id,tagset_id for fast queries.
  timescaledb.compress_segmentby = 'metric_id,tagset_id',
  timescaledb.compress_orderby = 'time',
  -- Combining chunks together when compressing makes queries faster and improves compression ratio slightly
  -- Where OpenTSDB would do 1 hour of data per row, we do 4 hours of data per row.
  timescaledb.compress_chunk_time_interval = '4 hours'
);

-- Since compression improves query speed, we should compress ASAP.
-- Note that writing data to a compressed row requires decompressing the row and recompressing it,
-- so ideally we only want to compress after we are pretty sure that the data is complete.
-- I.e. this value should not be less than the "past limit" for the metric streamer
-- See https://docs.timescale.com/use-timescale/latest/compression/modify-compressed-data/
select add_compression_policy('point', interval '2 hours');

-- This must match DATA_RETENTION_DAYS in the read API!
select  add_retention_policy('point', drop_after => INTERVAL '730 days', if_not_exists => true);

-- Tracks unique combinations of metric + tags (a "time series", equivalent to a tsuid in OTSDB)
-- Was not able to find a performant way to do metric=* queries for /api/search/lookup without this
create table time_series (
    metric_id smallint not null,
    tagset_id int not null,
    -- Used for incremental updates to tagset cache in query service
    created timestamp with time zone not null default now(),
    -- Used to make sure we don't delete time series prematurely
    last_used timestamp with time zone not null
);
create unique index time_series_metric_id_tagset_id_unique_idx on time_series (metric_id, tagset_id);

-- This trigger does not appear to add appreciable overhead to our writes,
-- and it saves us having to manage this manually.
create function ensure_time_series() returns trigger as $ensure_time_series$
    begin
        insert into time_series (metric_id, tagset_id, last_used)
        values (new.metric_id, new.tagset_id, new.time)
        on conflict (metric_id, tagset_id) do update set last_used = greatest(excluded.last_used, time_series.last_used);
        return new;
    end;
$ensure_time_series$ language plpgsql;

create trigger ensure_time_series after insert on point
	for each row execute function ensure_time_series();

-- Since this table was created later in the dev environment, we include this query to populate initial values
insert into time_series
select metric_id, tagset_id, now(), now() from (
	select distinct metric_id, tagset_id from point
) ts
on conflict do nothing;
