FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/RelaxAlpha.Api/RelaxAlpha.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown $APP_UID /data
COPY --from=backend /out/ ./
COPY --from=web /src/web/dist/relaxalpha/browser/ ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Market="Data Source=/data/relaxalpha.db"
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RelaxAlpha.Api.dll"]
