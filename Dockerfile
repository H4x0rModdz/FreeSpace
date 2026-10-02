FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached while only sources change.
COPY FreeSpace.slnx ./
COPY src/FreeSpace.Domain/FreeSpace.Domain.csproj src/FreeSpace.Domain/
COPY src/FreeSpace.Contracts/FreeSpace.Contracts.csproj src/FreeSpace.Contracts/
COPY src/FreeSpace.Infrastructure/FreeSpace.Infrastructure.csproj src/FreeSpace.Infrastructure/
COPY src/FreeSpace.Api/FreeSpace.Api.csproj src/FreeSpace.Api/
RUN dotnet restore src/FreeSpace.Api/FreeSpace.Api.csproj

COPY src/ src/
RUN dotnet publish src/FreeSpace.Api/FreeSpace.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FreeSpace.Api.dll"]
