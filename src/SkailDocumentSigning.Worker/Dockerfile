# Build context: repository root.
#   podman build -f src/SkailDocumentSigning.Worker/Dockerfile -t document-signing-worker .
# dotnet publish runs in the SDK image; the final image only needs the .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailDocumentSigning.Contracts/SkailDocumentSigning.Contracts.csproj src/SkailDocumentSigning.Contracts/
COPY src/SkailDocumentSigning.Worker/SkailDocumentSigning.Worker.csproj src/SkailDocumentSigning.Worker/
RUN dotnet restore src/SkailDocumentSigning.Worker/SkailDocumentSigning.Worker.csproj
COPY src/SkailDocumentSigning.Contracts src/SkailDocumentSigning.Contracts
COPY src/SkailDocumentSigning.Worker src/SkailDocumentSigning.Worker
RUN dotnet publish src/SkailDocumentSigning.Worker/SkailDocumentSigning.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SkailDocumentSigning.Worker.dll"]
