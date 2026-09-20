FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/BuildingBlocks/KJWebsite.BuildingBlocks/KJWebsite.BuildingBlocks.csproj", "src/BuildingBlocks/KJWebsite.BuildingBlocks/"]
COPY ["src/Services/ApiGateway/ApiGateway.csproj", "src/Services/ApiGateway/"]
RUN dotnet restore "src/Services/ApiGateway/ApiGateway.csproj"

COPY src/BuildingBlocks/KJWebsite.BuildingBlocks/ src/BuildingBlocks/KJWebsite.BuildingBlocks/
COPY src/Services/ApiGateway/ src/Services/ApiGateway/
RUN dotnet publish "src/Services/ApiGateway/ApiGateway.csproj" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
