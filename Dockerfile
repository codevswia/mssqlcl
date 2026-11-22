FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/mssqlcl/mssqlcl.csproj src/mssqlcl/
RUN dotnet restore src/mssqlcl/mssqlcl.csproj

COPY src/mssqlcl/ src/mssqlcl/
RUN dotnet publish src/mssqlcl/mssqlcl.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "mssqlcl.dll", "--mcp"]
