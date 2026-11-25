FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Web.Client/Web.Client.csproj", "src/Web.Client/"]
RUN dotnet restore "src/Web.Client/Web.Client.csproj"

COPY src/Web.Client/ src/Web.Client/
WORKDIR /src/src/Web.Client
RUN dotnet publish "Web.Client.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Web.Client.dll"]