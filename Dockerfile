# ---------- 1. Build the React frontend ----------
FROM node:22-alpine AS web
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---------- 2. Build the ASP.NET Core API ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY backend/CollaborateBench.Api/CollaborateBench.Api.csproj ./
RUN dotnet restore
COPY backend/CollaborateBench.Api/ ./
COPY --from=web /web/dist ./wwwroot
RUN dotnet publish -c Release -o /out --no-restore

# ---------- 3. Runtime: one container serves the API and the website ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "CollaborateBench.Api.dll"]
