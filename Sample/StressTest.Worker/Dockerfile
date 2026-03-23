FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files for restore (all referenced projects)
COPY Directory.Packages.props ./
COPY Sample/StressTest.Worker/StressTest.Worker.csproj Sample/StressTest.Worker/
COPY SimpleNetEngine.Client/SimpleNetEngine.Client.csproj SimpleNetEngine.Client/
COPY SimpleNetEngine.Protocol/SimpleNetEngine.Protocol.csproj SimpleNetEngine.Protocol/
COPY SimpleNetEngine.ProtoGenerator/SimpleNetEngine.ProtoGenerator.csproj SimpleNetEngine.ProtoGenerator/
COPY PacketParserGenerator/PacketParserGenerator.csproj PacketParserGenerator/
COPY Sample/Protocol.Sample.User/Protocol.Sample.User.csproj Sample/Protocol.Sample.User/
RUN dotnet restore Sample/StressTest.Worker/StressTest.Worker.csproj

# Copy source and build
COPY . .
RUN rm -f global.json
RUN dotnet publish Sample/StressTest.Worker/StressTest.Worker.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Copy signing key
COPY Sample/certs/server_signing_key.pub.pem /app/certs/

ENV DFRAME_CONTROLLER_ADDRESS=http://dframe-controller:7313
ENV REDIS_CONNECTION=redis:6379
ENV GATEWAY_HOST=gateway
ENV GATEWAY_PORT=5000

ENTRYPOINT ["dotnet", "StressTest.Worker.dll"]
