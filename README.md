# Metric Read API

Schema and JSON HTTP API query layer for TimescaleDB which (mostly) matches the data contract and behaviour of [OpenTSDB](http://opentsdb.net/docs/build/html/api_http/query/index.html).

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
