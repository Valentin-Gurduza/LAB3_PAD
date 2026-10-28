FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Warehouse.Node/Warehouse.Node.csproj src/Warehouse.Node/
RUN dotnet restore src/Warehouse.Node/Warehouse.Node.csproj
COPY src/Warehouse.Node/ src/Warehouse.Node/
RUN dotnet publish src/Warehouse.Node/Warehouse.Node.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Warehouse.Node.dll"]
