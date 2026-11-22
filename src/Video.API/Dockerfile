FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Video.Contracts/Video.Contracts.csproj", "src/Video.Contracts/"]
COPY ["src/Video.API/Video.API.csproj", "src/Video.API/"]
RUN dotnet restore "src/Video.API/Video.API.csproj"

COPY src/Video.Contracts/ src/Video.Contracts/
COPY src/Video.API/ src/Video.API/
WORKDIR /src/src/Video.API
RUN dotnet publish "Video.API.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Video.API.dll"]