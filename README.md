# Timescale-based OpenTSDB Drop-in Replacement

Schema and JSON HTTP API query layer for [TimescaleDB](https://github.com/timescale/timescaledb) which (mostly) matches the data contract and behaviour of [OpenTSDB](http://opentsdb.net/docs/build/html/api_http/query/index.html).

# Why should I use this?

 - **Less complexity**: OpenTSDB is nearly 120k lines of code (closer to 145k if you count its AsyncHBase library too) and runs on top of the extremely complex HBase ([the user manual for HBase](https://hbase.apache.org/book.html) is a 26-hour read according to Firefox's reader mode estimate). This Timescale adapter layer is less than 4500 lines of code, and 1500 of those lines are for the included graph UI frontend.

 - **Better visibility over your data**: OpenTSDB stores data in HBase using a bespoke binary format. Reading or manipulating this data with tools other than OpenTSDB itself is functionally impossible. Timescale, however, is simply a PostGreSQL extension. Your data can be queried not only with the OpenTSDB adapter layer, but also by directly connecting to Timescale and running standard PostGreSQL statements. You can use this to craft specialised queries that would be impossible with OpenTSDB's rigidly prescribed access pattern.

 - **Improved performance**: The schema and query pattern adopted by this adapter layer avoids the [cardinality problem](https://opentsdb.net/docs/build/html/user_guide/writing/index.html#time-series-cardinality) that causes poor query performance in OpenTSDB when you have many time series within the same metric and run queries that only return a small subset of those series. Production deployments of small to medium size installations have shown a 15x improvement in real-world query response times, while running on 5x less hardware.

## Quick start

First, adjust [DDL.sql](./DDL.sql) to your liking. Pay specific attention to things like retention intervals, chunk sizes, column ordering in indices, etc. You should tune these to match your use case.

There are also settings for the API which can be tuned via envvars. These are detailed in [./ReadApi/Settings.cs](./ReadApi/Settings.cs). Pay particular attention to matching `DATA_RETENTION_DAYS` to the retention interval that you have configured in Timescale itself.

```shell
echo "TIMESCALE_PASSWORD=<whatever_you_like>" >> .env
echo "DATA_RETENTION_DAYS=<match_ddl>" >> .env
```

Once you are happy with your schema and API settings, ensure you have a recent version of docker-compose (v2.31.0 has been tested to work). Then:

```shell
docker-compose up -d
```

Timescale should now be available on port 5432, and the OTSDB-like API on port 8080.

## Developer Prerequisites for the API

 - [Dotnet SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## Running the API

Create a `.env` file in the repo root and fill it with the following lines (replace placeholders with real values):

```
TIMESCALE_HOST=<timescale hostname/IP>
TIMESCALE_PORT=<timescale port e.g. 5432>
TIMESCALE_USER=<username e.g. postgres>
TIMESCALE_PASSWORD=<your password here>
TIMESCALE_DBNAME=<catalog name e.g. postgres>
```

A launch.json is provided for running/debugging with VS Code. However, you can also run with the dotnet CLI:

```shell
dotnet build

cd ReadApi
dotnet run

cd ../Tests
dotnet test
```

## Building the API

The build is dockerised. To build locally:

```shell
docker build --tag=read-api .
```

and to run the image:

```shell
docker run --rm --env-file .env read-api
```

To build outside of docker, use the Dockerfile as an instruction manual.
