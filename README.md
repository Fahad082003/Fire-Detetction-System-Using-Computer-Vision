# Sentinel — Emergency Monitoring Command Center

Real-time fire/threat monitoring. External AI services send **pre-computed inference JSON**: bounding boxes, danger zones and fire severity. Sentinel draws that JSON live over CCTV and drone feeds. It also opens and escalates incidents, sends SMS, email and web-push alerts, and switches on smart sprinklers automatically.

> The AI/computer-vision models are **not** part of this repository. They run as an external service.

| Layer | Technology |
|---|---|
| Frontend | Angular 22 (standalone components, signals, RxJS), TypeScript, Tailwind CSS 4, HTML5 Canvas, hls.js |
| Backend | ASP.NET Core 8 Web API (C#), controllers, services, interfaces, DTOs, EF Core 8 |
| Real-time | SignalR hub at `/hubs/monitoring` |
| Database | Microsoft SQL Server (EF Core migrations) |
| Notifications | Twilio SMS, SMTP email (MailKit), Web Push (VAPID) |
| IoT simulation | `Sentinel.Simulator` (.NET console): smoke/temperature sensors, sprinklers, AI frames |

```
AI service / IoT gateway ──(X-Device-Key)──► ASP.NET Core API ──► EF Core ──► SQL Server
      ▲  Sentinel.Simulator                     │  AlertEngine → NotificationWorker → SMS / Email / Push
      │                                         │  SprinklerService (auto-activation)
      │                                         ▼
      └───────────────────────────── SignalR /hubs/monitoring ──► Angular dashboard (JWT)
```

## Quick start (Windows PowerShell)

Prerequisites: .NET 8 SDK or newer, Node.js 20.19+ with the Angular CLI (`npm i -g @angular/cli`), SQL Server (Developer, Express or LocalDB), and `dotnet tool install --global dotnet-ef`.

```powershell
cd C:\Users\m_fahad\Desktop\Project
Copy-Item .env.example .env        # then set Jwt__SigningKey, Device__ApiKey, Seed__AdminPassword

# Backend
cd backend
dotnet restore
dotnet build
dotnet ef database update --project Sentinel.Api
dotnet run --project Sentinel.Api --launch-profile http     # http://localhost:5291/swagger

# Frontend (new terminal)
cd C:\Users\m_fahad\Desktop\Project\frontend
npm install
ng serve                                                    # http://localhost:4200

# Simulator (new terminal): fire in Zone B
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet run --project Sentinel.Simulator -- --device-key <Device__ApiKey> --ignite "Zone B"
```

Log in with `Seed__AdminEmail` and `Seed__AdminPassword`. The admin account is created on the first API start, when the Users table is empty. In Development, demo cameras, sensors and sprinklers are seeded automatically.

## Tests

```powershell
cd backend;  dotnet test                  # unit tests + API/SignalR integration tests (SQLite in-memory)
cd frontend; ng test --watch=false        # Vitest unit tests
cd frontend; ng build                     # production build
```

## Documentation

The full beginner guide in Roman Urdu covers architecture, every file, the API, the testing flow and troubleshooting: **[docs/PROJECT_GUIDE_ROMAN_URDU.md](docs/PROJECT_GUIDE_ROMAN_URDU.md)**

**[docs/SENTINEL_GUIDE_ROMAN_URDU_v2.pdf](docs/SENTINEL_GUIDE_ROMAN_URDU_v2.pdf)** (source: [.md](docs/SENTINEL_GUIDE_ROMAN_URDU_v2.md)) is a Roman Urdu guide to the v2 features:
- Safe zones and the backend danger-zone person check.
- Fire-growth history and logs (`GET /api/incidents/logs`).
- Floor-plan upload and editor (`/api/floorplans`).
- Evacuation route and estimated time (`POST /api/floorplans/{id}/route`).

Apply the new migration with `dotnet ef database update --project Sentinel.Api`.
