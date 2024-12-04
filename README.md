# Metric Read API

Schema and JSON HTTP API query layer for TimescaleDB which (mostly) matches the data contract and behaviour of [OpenTSDB](http://opentsdb.net/docs/build/html/api_http/query/index.html).

## Quick start

First, adjust [DDL.sql](./DDL.sql) to your liking. Pay specific attention to things like retention intervals, chunk sizes, column ordering in indices, etc. You should tune these to match your use case.

Once you are happy with your schema, ensure you have a recent version of docker-compose (v2.31.0 has been tested to work). Then:

```bash
TIMESCALE_PASSWORD=whatever_you_like docker-compose up -d
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

It is recommended to use VS Code (a launch.json is included) for running/debugging. However, you can also run with the dotnet CLI:

```
dotnet build

cd ReadApi
dotnet run

cd ../Tests
dotnet test
```

## Building the API

The build is dockerised. To build locally:

```
docker build --tag=read-api .
```

and to run the image:

```
docker run --rm --env-file .env read-api
```

To build outside of docker, use the Dockerfile as an instruction manual.
