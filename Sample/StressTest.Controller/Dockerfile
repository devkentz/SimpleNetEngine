FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files for restore
COPY Directory.Packages.props ./
COPY Sample/StressTest.Controller/StressTest.Controller.csproj Sample/StressTest.Controller/
RUN dotnet restore Sample/StressTest.Controller/StressTest.Controller.csproj

# Copy source and build
COPY . .
RUN rm -f global.json
RUN dotnet publish Sample/StressTest.Controller/StressTest.Controller.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV DFRAME_CONTROLLER_PORT=7312
ENV DFRAME_WORKER_PORT=7313

EXPOSE 7312 7313

ENTRYPOINT ["dotnet", "StressTest.Controller.dll"]
