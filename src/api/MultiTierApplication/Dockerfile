# ---- Stage 1: build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish MultiTierApplication.csproj -c Release -o /app/publish --no-restore

# ---- Stage 2: runtime ----
# FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER 1654
ENTRYPOINT ["dotnet", "MultiTierApplication.dll"]

# ---- Stage: build the migration bundle ----
FROM build as migration-build
RUN dotnet tool install --global dotnet-ef
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
	--project MultiTierApplication.csproj \
	--self-contained -r linux-x64 \
	--output /app/efbundle

# ---- Stage: the migrator itself ----
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /app
COPY --from=migration-build /app/efbundle .
ENTRYPOINT ["./efbundle"]