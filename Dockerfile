ARG CI_DEPENDENCY_PROXY_DIRECT_GROUP_IMAGE_PREFIX=docker.io
ARG BUILDPLATFORM=linux/amd64
ARG TARGETPLATFORM=linux/amd64

FROM ${CI_DEPENDENCY_PROXY_DIRECT_GROUP_IMAGE_PREFIX}/node:21-slim AS frontend_builder

    RUN corepack enable

    WORKDIR /app

    ADD frontend/package.json .
    ADD frontend/.yarnrc.yml .
    ADD frontend/yarn.lock .

    RUN yarn install --immutable

    ADD frontend/index.html .
    ADD frontend/favicon favicon
    ADD frontend/tsconfig.json .
    ADD frontend/tsconfig.node.json .
    ADD frontend/vite.config.ts .
    ADD frontend/src src
    ADD frontend/fomantic-ui-less fomantic-ui-less

    RUN yarn run build

FROM frontend_builder AS frontend_ci

    RUN yarn run lint:lockfile

    ADD frontend/.eslintrc.cjs .
    RUN yarn run lint

    ADD frontend/.ecrc .
    ADD frontend/.editorconfig .

    # Does the same thing as `yarn run lint:editorconfig` but caches the binary in the docker cache, avoiding issues with API rate limits
    ADD https://github.com/editorconfig-checker/editorconfig-checker/releases/download/v3.0.0/ec-linux-amd64.tar.gz editorconfig-checker/
    RUN tar -C editorconfig-checker -xzf editorconfig-checker/ec-linux-amd64.tar.gz
    RUN ./editorconfig-checker/bin/ec-linux-amd64

    ADD frontend/.dependency-cruiser.cjs .
    RUN yarn run depcruise

    ENTRYPOINT ["echo", "CI checks passed!"]

FROM ${CI_DEPENDENCY_PROXY_DIRECT_GROUP_IMAGE_PREFIX}/judilsteve/vsfh-compressor:1.0 AS compressor

    COPY --from=frontend_builder /app/dist /static

    RUN /app/VerySimpleFileHostCompressor /static

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

    WORKDIR /tmp
    COPY . /tmp/readapi
    WORKDIR /tmp/readapi

    RUN dotnet publish ReadApi \
        -c Release \
        -o build \
        --self-contained -r $([ ${TARGETPLATFORM} = "linux/arm64" ] && echo "linux-arm64" || echo "linux-x64") \
        -p:PublishReadyToRun=true \
        -p:TreatWarningsAsErrors=true

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS backend_ci

    WORKDIR /tmp
    COPY . /tmp/readapi
    WORKDIR /tmp/readapi

    RUN dotnet test -r $([ ${TARGETPLATFORM} = "linux/arm64" ] && echo "linux-arm64" || echo "linux-x64")

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0

    COPY --from=builder /tmp/readapi/build /readapi
    COPY --from=compressor /static /readapi/frontend

    WORKDIR /readapi
    ENTRYPOINT [ "/readapi/read-api" ]
