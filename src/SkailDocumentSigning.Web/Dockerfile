# Build context: repository root.
#   podman build -f src/SkailDocumentSigning.Web/Dockerfile -t document-signing-web .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailDocumentSigning.Contracts/SkailDocumentSigning.Contracts.csproj src/SkailDocumentSigning.Contracts/
COPY src/SkailDocumentSigning.Web/SkailDocumentSigning.Web.csproj src/SkailDocumentSigning.Web/
RUN dotnet restore src/SkailDocumentSigning.Web/SkailDocumentSigning.Web.csproj
COPY src/SkailDocumentSigning.Contracts src/SkailDocumentSigning.Contracts
COPY src/SkailDocumentSigning.Web src/SkailDocumentSigning.Web
RUN dotnet publish src/SkailDocumentSigning.Web/SkailDocumentSigning.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SkailDocumentSigning.Web.dll"]
