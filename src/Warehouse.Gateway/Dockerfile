FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Warehouse.Gateway/Warehouse.Gateway.csproj src/Warehouse.Gateway/
RUN dotnet restore src/Warehouse.Gateway/Warehouse.Gateway.csproj
COPY src/Warehouse.Gateway/ src/Warehouse.Gateway/
RUN dotnet publish src/Warehouse.Gateway/Warehouse.Gateway.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Warehouse.Gateway.dll"]
