FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Video.Contracts/Video.Contracts.csproj", "src/Video.Contracts/"]
COPY ["src/Video.Worker/Video.Worker.csproj", "src/Video.Worker/"]
RUN dotnet restore "src/Video.Worker/Video.Worker.csproj"

COPY src/Video.Contracts/ src/Video.Contracts/
COPY src/Video.Worker/ src/Video.Worker/
WORKDIR /src/src/Video.Worker
RUN dotnet publish "Video.Worker.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ffmpeg \
	&& rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Video.Worker.dll"]