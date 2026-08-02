# Running GigaClaw on Linux Servers & Docker Containers

**Yes, in its current state, GigaClaw can run directly on any Linux server and inside Docker containers without code modifications.**

The application is built on ASP.NET Core (.NET 10.0) and Cloud-Ready cross-platform standards.

---

## 1. Direct Execution on a Linux Server

### Quick Start (Bare Metal / VM)
1. **Install .NET 10.0 Runtime or SDK** (Ubuntu/Debian example):
   ```bash
   sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
   ```

2. **Run GigaClaw**:
   ```bash
   # From source directory:
   ./run.sh --urls "http://0.0.0.0:5230"

   # Or from published release build:
   dotnet GigaClaw.Web.dll --urls "http://0.0.0.0:5230"
   ```

### Systemd Service Configuration (Background Daemon)
To run GigaClaw as a persistent Linux daemon, create `/etc/systemd/system/gigaclaw.service`:

```ini
[Unit]
Description=GigaClaw Kanban Automation Web App
After=network.target

[Service]
Type=simple
User=gigaclaw
WorkingDirectory=/opt/gigaclaw
ExecStart=/usr/bin/dotnet /opt/gigaclaw/GigaClaw.Web.dll --urls "http://0.0.0.0:5230"
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=GIGACLAW_DATA_DIR=/var/lib/gigaclaw

[Install]
WantedBy=multi-user.target
```

Enable and start the service:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now gigaclaw
```

---

## 2. Docker Container Execution

GigaClaw can be containerized using a multi-stage Dockerfile.

### `Dockerfile`
Create `Dockerfile` in the root of the repo:

```dockerfile
# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["GigaClaw.slnx", "."]
COPY ["GigaClaw.Core/GigaClaw.Core.csproj", "GigaClaw.Core/"]
COPY ["GigaClaw.Web/GigaClaw.Web.csproj", "GigaClaw.Web/"]
COPY ["GigaClaw.ClaudeMock/GigaClaw.ClaudeMock.csproj", "GigaClaw.ClaudeMock/"]
COPY ["GigaClaw.QaRunner/GigaClaw.QaRunner.csproj", "GigaClaw.QaRunner/"]
RUN dotnet restore GigaClaw.slnx

# Copy full source and publish
COPY . .
RUN dotnet publish GigaClaw.Web/GigaClaw.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Optional: Install Node.js & Claude CLI inside container if running agent dispatches
RUN apt-get update && apt-get install -y curl nodejs npm git && \
    npm install -g @anthropic-ai/claude-code && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 5230
ENV ASPNETCORE_URLS="http://0.0.0.0:5230"
ENV GIGACLAW_DATA_DIR="/app/data"

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "GigaClaw.Web.dll"]
```

### `docker-compose.yml`
Create `docker-compose.yml`:

```yaml
version: '3.8'

services:
  gigaclaw:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: gigaclaw
    restart: unless-stopped
    ports:
      - "5230:5230"
    volumes:
      - gigaclaw_data:/app/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - GIGACLAW_DATA_DIR=/app/data

volumes:
  gigaclaw_data:
```

### Running with Docker Compose:
```bash
docker compose up -d --build
```

---

## 3. Compatibility Summary Matrix

| Feature | Linux Server (Bare Metal / VM) | Docker Container | Notes |
| :--- | :---: | :---: | :--- |
| **ASP.NET Core Web Engine** | ✅ Supported | ✅ Supported | Runs natively on Kestrel |
| **SQLite Persistence** | ✅ Supported | ✅ Supported | Mounted to persistent volume |
| **REST & SignalR/WebSockets** | ✅ Supported | ✅ Supported | Port 5230 |
| **Windows Platform Features** | ⚠️ Automatically Gated | ⚠️ Automatically Gated | `WindowsFolderPicker` disabled gracefully |
| **Agent CLI Dispatches** | ✅ Supported | ✅ Supported | Requires Node/Claude CLI installed in OS/image |
