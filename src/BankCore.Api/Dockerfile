FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/BankCore.Domain/BankCore.Domain.csproj src/BankCore.Domain/
COPY src/BankCore.Infrastructure/BankCore.Infrastructure.csproj src/BankCore.Infrastructure/
COPY src/BankCore.Api/BankCore.Api.csproj src/BankCore.Api/
RUN dotnet restore src/BankCore.Api/BankCore.Api.csproj
COPY src/ src/
RUN dotnet publish src/BankCore.Api/BankCore.Api.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BankCore.Api.dll"]
