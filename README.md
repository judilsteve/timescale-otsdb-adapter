# Metric Read API

Schema and JSON HTTP API query layer for TimescaleDB which (mostly) matches the data contract and behaviour of [OpenTSDB](http://opentsdb.net/docs/build/html/api_http/query/index.html).

## Developer Prerequisites

 - [Dotnet SDK 9.0](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## Running

Create a `.env` file in the repo root and fill it with the following lines (replace placeholders with real values):

```
TIMESCALE_HOST=<timescale hostname/IP>
TIMESCALE_PORT=<timescale port e.g. 5432>
TIMESCALE_USER=<username e.g. postgres>
TIMESCALE_PASSWORD=<your password here>
TIMESCALE_DBNAME=<catalog name e.g. postgres>
```

It is recommended to use VS Code (a launch.json is included) for running/debugging. However, you can also run with the dotnet CLI:

```
dotnet build

cd ReadApi
dotnet run

cd ../Tests
dotnet test
```

## Building

The build is dockerised. To build locally:

```
docker build --tag=read-api .
```

and to run the image:

```
docker run --rm --env-file .env read-api
```
