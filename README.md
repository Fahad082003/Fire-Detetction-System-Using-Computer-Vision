# FireVision AI — Emergency Monitoring Command Center

Real-time fire/threat monitoring. Its own AI service (or any producer that posts the same JSON) sends bounding boxes, danger zones and fire severity, and FireVision AI draws them live over CCTV and drone feeds. It also:
- opens and escalates incidents, and sends SMS, email and web-push alerts;
- switches on smart sprinklers automatically;
- raises a **camera lost** alert when a camera goes silent;
- places people seen by calibrated cameras on the floor plan, and computes an evacuation route from where they stand;
- predicts which rooms a fire may reach next (rule-based, through doors) and shows a risk heatmap on the floor plan.

> AI inference runs in a separate Python service, [ai-service/](ai-service/README.md), using ONNX Runtime on CPU.
> - **Person detection** (YOLOX-Tiny, Apache-2.0) is installed and was evaluated on 2026-10-13: see [ai-service/reports](ai-service/reports/README.md).
> - **Fire/smoke detection** (YOLOX-Tiny fine-tuned on D-Fire) is installed and was evaluated on 2026-10-15: see [ai-service/training](ai-service/training/README.md).
> - Both model files (`ai-service/models/*.onnx`, about 20 MB each) are in the repo, so a fresh clone needs no download.
>
> The dashboard's **AI** tab shows which models are actually loaded.

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

# AI inference service (new terminal), see ai-service/README.md
cd C:\Users\m_fahad\Desktop\Project\ai-service
py -3.13 -m venv .venv; .\.venv\Scripts\python -m pip install -r requirements.txt
.\.venv\Scripts\python tools\download_models.py; .\.venv\Scripts\python -m sentinel_ai    # http://127.0.0.1:8008/health

# Simulator (new terminal): fire in the warehouse
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet run --project Sentinel.Simulator -- --device-key <Device__ApiKey> --ignite "Ground Floor - Warehouse"
```

After the first setup, `.\start.ps1` opens the API, the dashboard and the AI service in their own windows (`.\start.ps1 -Simulator` adds the fire demo). It reads `Device__ApiKey` from `.env` and hands it to the AI service.

Log in with `Seed__AdminEmail` and `Seed__AdminPassword`. The admin account is created on the first API start, when the Users table is empty. In Development, demo cameras, sensors and sprinklers are seeded automatically. An Admin adds, edits and deletes the customer's own cameras on the **Devices** page. Stream passwords are masked for dashboard users and sent only to the AI service. When `SENTINEL_DEVICE_KEY` is set, the AI service reloads its camera list from the API every 60 s, so a camera added there is analysed within a minute without editing `config.json`. For a live test without an IP camera, `ai-service\tools\mjpeg_camera.py 0` serves the laptop webcam (or a video file) as MJPEG at `http://127.0.0.1:8090/video`. Add it on the Devices page with format Mjpeg. `ai-service\tools\live_test.py` runs the whole chain automatically with `samples/fire-test.avi` (D-Fire test frames, CC0): camera, floor plan, calibration, AI fire incident, danger zone, re-routed evacuation and the risk heatmap, then prints PASS/FAIL. It refuses to run while real SMS or email settings are in `.env`.

## Same database on two PCs (private repo only)

The database travels through git as a backup file, `database/SentinelDb.bak`. It holds the login user and every record, so keep the repo private.

```powershell
# PC 1: finishing work
.\database\save.ps1
git add database\SentinelDb.bak; git commit -m "db: update"; git push

# PC 2: starting work (stop the API first; this OVERWRITES the local database)
git pull
.\database\load.ps1
```

- The last `save` wins. Do not change data on both PCs between syncs.
- The second PC needs SQL Server 2022 or newer, because a backup cannot be restored on an older version.
- If PowerShell blocks the scripts, run: `powershell -ExecutionPolicy Bypass -File .\database\save.ps1`.

## Tests

```powershell
cd backend;  dotnet test                  # unit tests + API/SignalR integration tests (SQLite in-memory)
cd frontend; ng test --watch=false        # Vitest unit tests
cd frontend; ng build                     # production build
cd ai-service; .\.venv\Scripts\python -m unittest discover -s tests   # AI service (mock-based)
cd frontend; npm run e2e                  # browser tests in Edge, all API calls mocked (see below)
```

On 2026-10-19 these passed: backend 64, frontend 29, AI service 24, browser 5.
- The browser tests (`frontend/e2e`, Playwright with the installed Edge) answer every request to `localhost:5291` inside the browser.
- While they run, a tripwire listens on port 5291 and fails the run if anything connects. So they never touch a real API or database.

Model evaluation tools live in `ai-service/tools`:
- `evaluate.py`: precision, recall, F1, AP50 and AP50:95, and latency, measured on a labelled set.
- `degrade.py`: synthetic darkness and fog.
- `coco_to_yolo.py`: COCO labels in the format `evaluate.py` reads.

Results and the full audit are in [ai-service/reports](ai-service/reports/README.md). The project guide, chapter 27, lists every tool and its command.

## Documentation

**[docs/FIREVISION_AI_GUIDE_ROMAN_URDU_v5.pdf](docs/FIREVISION_AI_GUIDE_ROMAN_URDU_v5.pdf)** is the full project guide in Roman Urdu. The same guide is also a browsable page, **[docs/FIREVISION_AI_GUIDE_ROMAN_URDU_v5.html](docs/FIREVISION_AI_GUIDE_ROMAN_URDU_v5.html)**, with a chapter sidebar, search and dark mode. Open it from the `docs` folder so the screenshots load. It covers:
- The whole flow from camera to dashboard, alerts, sensors, sprinklers, floor plans and evacuation.
- The database, security, the API and SignalR.
- The AI models: where each model came from, the D-Fire dataset and its image counts, the 80-epoch Kaggle training, and the measured accuracy.
- The end-to-end test, how to run everything, requirement status and the work that remains.

**[docs/FIREVISION_AI_USER_GUIDE_ROMAN_URDU.pdf](docs/FIREVISION_AI_USER_GUIDE_ROMAN_URDU.pdf)** is a step-by-step guide for non-technical users. It is also a browsable page, **[docs/FIREVISION_AI_USER_GUIDE_ROMAN_URDU.html](docs/FIREVISION_AI_USER_GUIDE_ROMAN_URDU.html)**, where the test checkboxes can be ticked and the page remembers them. It covers:
- Installing, starting and logging in.
- What every part of the dashboard does.
- Nine hands-on tests, each with the expected result.
- A "Safe Test Mode" that keeps real SMS and email from being sent during tests.
- Troubleshooting and a printable checklist.

**[docs/FIREVISION_AI_FEATURES_GUIDE_ROMAN_URDU.html](docs/FIREVISION_AI_FEATURES_GUIDE_ROMAN_URDU.html)** explains how to use each feature: where to click, what to fill in and what you should see. It covers adding cameras, drawing a floor plan, calibration, evacuation routes, the risk heatmap, alerts, sprinklers, dispatch, the simulator and moving the database between PCs.
