# Hosts the precompiled ASP.NET MVC app with Mono + XSP (for macOS/Linux, where IIS is unavailable).
FROM mono:6.12
RUN sed -i 's|deb.debian.org|archive.debian.org|g; s|security.debian.org|archive.debian.org|g; /buster-updates/d' /etc/apt/sources.list \
 && apt-get -o Acquire::Check-Valid-Until=false update \
 && apt-get install -y --no-install-recommends mono-xsp4 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
CMD ["xsp4","--port","8080","--address","0.0.0.0","--nonstop"]
