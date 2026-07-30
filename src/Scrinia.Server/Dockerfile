FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS restore
WORKDIR /build
COPY Directory.Build.targets ./
COPY Directory.Build.props ./
COPY src/Scrinia.Core/Scrinia.Core.csproj src/Scrinia.Core/
COPY src/Scrinia.Mcp/Scrinia.Mcp.csproj src/Scrinia.Mcp/
COPY src/Scrinia.Plugin.Abstractions/Scrinia.Plugin.Abstractions.csproj src/Scrinia.Plugin.Abstractions/
COPY src/Scrinia.Server/Scrinia.Server.csproj src/Scrinia.Server/
COPY src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj src/Scrinia.Plugin.Embeddings/
RUN dotnet restore src/Scrinia.Server/Scrinia.Server.csproj && \
    dotnet restore src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj

FROM node:24.15.0-slim AS web-build
WORKDIR /web
COPY web/package*.json ./
RUN npm install
COPY web/ .
RUN npx tsc -b && npx vite build --outDir dist

# Download models during build so they're baked into the image
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS model-download
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# Model2Vec (built-in, ~22MB), pinned to a specific commit revision with checksum verification
RUN mkdir -p /models/m2v-MiniLM-L6-v2 && \
    curl -fSL -o /models/m2v-MiniLM-L6-v2/model.safetensors \
    "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/cbcfbe8e8310fa56fdfb91c4293df80e7e0cb397/model.safetensors" && \
    echo "0792db26b8391f89224e825fb3ed3f35498d8bda4936b92fa7658b4dda90cf28  /models/m2v-MiniLM-L6-v2/model.safetensors" | sha256sum -c - && \
    curl -fSL -o /models/m2v-MiniLM-L6-v2/vocab.txt \
    "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/cbcfbe8e8310fa56fdfb91c4293df80e7e0cb397/vocab.txt" && \
    echo "eef2f6b6ea4166090d7e6c1feec524b17a105c621847f3f99c9af20291d3cee1  /models/m2v-MiniLM-L6-v2/vocab.txt" | sha256sum -c -
# Vulkan GGUF (optional, ~24MB), pinned to a specific commit revision with checksum verification
RUN curl -fSL -o /models/all-MiniLM-L6-v2-Q8_0.gguf \
    "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/544f204f2eaa2d71361ffc74d6df7170285b286a/all-MiniLM-L6-v2-Q8_0.gguf" && \
    echo "263215c3cadd6e16740741a7624ab4cbb6c8e777688bd5331ecfbf5681c2f8ed  /models/all-MiniLM-L6-v2-Q8_0.gguf" | sha256sum -c -

FROM restore AS build
COPY src/Scrinia.Core/ src/Scrinia.Core/
COPY src/Scrinia.Mcp/ src/Scrinia.Mcp/
COPY src/Scrinia.Plugin.Abstractions/ src/Scrinia.Plugin.Abstractions/
COPY src/Scrinia.Server/ src/Scrinia.Server/
COPY src/Scrinia.Plugin.Embeddings/ src/Scrinia.Plugin.Embeddings/
COPY --from=web-build /web/dist src/Scrinia.Server/wwwroot/
RUN dotnet build src/Scrinia.Server/Scrinia.Server.csproj -c Release --no-restore && \
    dotnet build src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/Scrinia.Server/Scrinia.Server.csproj -c Release --no-build -o /app/publish && \
    dotnet publish src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj -c Release --no-build -o /app/plugins

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
RUN groupadd -r scrinia && useradd -r -g scrinia -m scrinia
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /app/plugins plugins/
COPY --from=model-download /models/m2v-MiniLM-L6-v2 models/m2v-MiniLM-L6-v2/
COPY --from=model-download /models/all-MiniLM-L6-v2-Q8_0.gguf plugins/scri-plugin-embeddings/all-MiniLM-L6-v2-Q8_0.gguf
RUN mkdir -p /data && chown -R scrinia:scrinia /data
ENV ASPNETCORE_URLS=http://+:8080
ENV Scrinia__DataDir=/data
# Set Scrinia__Embeddings__Vulkan=true to enable GPU-accelerated embeddings
ENV Scrinia__Embeddings__Vulkan=false
USER scrinia
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Scrinia.Server.dll"]
