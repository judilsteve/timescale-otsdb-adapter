# Timescale Grapher

Intended as a replacement for OpenTSDB's [simple GUI](http://opentsdb.net/docs/build/html/user_guide/guis/index.html)

## Getting Started

See the `Dockerfile` for required Node version. To install packages:

```
corepack enable # This will make sure that the correct version of yarn is used
yarn install --immutable
```

### Run a local development (hot-reload) server with a mock backend

```
yarn run dev
```

### Run a local development (hot-reload) server with a local development backend

```
echo 'VITE_DEV_PROXY_TARGET=http://localhost:5115/' > .env.local
yarn run dev
```

### Run linting

```
yarn run lint
yarn run lint:lockfile
yarn run lint:editorconfig
```

## UI Development

UI is handled by [Semantic UI with React bindings](https://react.semantic-ui.com/).
