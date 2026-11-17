# The Weblitz showcase API as a container. Built from this folder alone (it references the WeblitzPdf package):
#   docker build -t weblitz-url-to-pdf src
#   docker run --rm -p 8080:8080 -e SSRS2_LICENSE=... weblitz-url-to-pdf
#   curl -o page.pdf "http://localhost:8080/pdf?url=https://example.com"
# (the build runs on the machine's own platform and publishes for the image's: no emulation for the compiler)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Published for the image's own platform: the package carries the engine's native libraries for every platform
# (about 900 MB in a portable publish), of which a container needs one
ARG TARGETARCH
WORKDIR /src
COPY WeblitzPdf.Sample.UrlToPdf.csproj ./
RUN rid=linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet restore -r "$rid"
COPY . ./
RUN rid=linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet publish -c Release -r "$rid" --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Weblitz carries its own engine and the fonts drawn for Arial, Times New Roman, Courier New, Calibri, Cambria and
# Segoe UI. Pages in Arabic, Chinese, Japanese, Korean, Thai or the Indic scripts, and emoji, are drawn with the
# machine's fonts: a server image has none unless they are installed (else such text prints as empty boxes).
RUN apt-get update && apt-get install -y --no-install-recommends \
      libfontconfig1 fonts-noto-core fonts-noto-cjk fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# .NET reaches its optimised code sooner with this (an environment variable only: no project setting carries it)
ENV DOTNET_TieredPGO_InstrumentOnlyHotCode=0 \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "weblitz-url-to-pdf.dll"]
