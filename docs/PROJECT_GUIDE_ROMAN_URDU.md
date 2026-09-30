# Sentinel — Emergency Monitoring Command Center
## Mukammal Project Guide (Aasan Roman Urdu)

> **Kis ke liye:** naye developers, students, aur har woh shakhs jo is project ko pehli dafa chala raha hai.
> **Stack:** Angular 22 + Tailwind CSS (frontend) · ASP.NET Core 8 Web API + SignalR (backend) · SQL Server + EF Core (database) · Twilio / Email / Web Push (notifications) · .NET console simulator (IoT + AI mock data).
> **Note:** AI/Computer Vision ka koi code is project mein **nahi** hai. AI ek bahar ki service hai jo sirf tayyar (pre-computed) JSON bhejti hai.

---

## Fehrist (Table of Contents)

1. [Pora Project Asal Mein Hai Kya?](#1-pora-project-asal-mein-hai-kya)
2. [Architecture & File Structure](#2-architecture--file-structure)
3. [Step-by-Step Chalane Ka Tariqa (Execution Guide)](#3-step-by-step-chalane-ka-tariqa-execution-guide)
4. [Testing Flow — Swagger / Postman se Mock AI Data](#4-testing-flow--swagger--postman-se-mock-ai-data)
5. [Common Troubleshooting](#5-common-troubleshooting)
6. [Kya Verify Hua, Kya Nahi](#6-kya-verify-hua-kya-nahi)

---

## 1. Pora Project Asal Mein Hai Kya?

### 1.1 Aik line mein

Sentinel ek **"control room" software** hai. Cameras (CCTV + Drone) aur sensors (Smoke + Temperature) se aane wala data **live** screen par dikhata hai. Aag ya khatre mein phanse insaan nazar aayein to yeh khud:

- **Alarm** bajata hai,
- **SMS / Email / Push** notification bhejta hai,
- **Sprinklers** (paani ke fawware) on kar deta hai.

### 1.2 Real-life misaal

Ek bari factory ka security room sochiye. Deewar par bohat se TV screens hain. Ek guard sab ko dekh raha hai. Sentinel us guard ka **computer version** hai:

| Asal duniya | Sentinel mein |
|---|---|
| CCTV camera ki screen | `VideoFeedComponent` (Angular) |
| Guard ki aankh jo aag pehchaanti hai | External **AI service** (hamara code nahi) |
| Guard ki notebook jahan har waqia likha jata hai | SQL Server ka `IncidentAlerts` table |
| Guard ka dimagh jo faisla karta hai "yeh kitna khatarnak hai" | `AlertEngine.cs` (backend) |
| Guard ka phone jis se woh fire brigade ko call karta hai | `NotificationWorker` + Twilio/Email/Push |
| Deewar par laga sprinkler ka switch | `SmartSprinkler` + `SprinklerService.cs` |
| Radio jis se har guard ko foran khabar milti hai | **SignalR** (real-time connection) |

### 1.3 Kuch zaroori alfaaz (asaan matlab)

- **API:** Backend ka ek "darwaza" jahan frontend request bhejta hai, jaise `POST /api/detections`.
- **REST:** HTTP ke zariye request/response ka tareeqa: GET (lao), POST (bhejo/banao), PUT (badlo), DELETE (mitao).
- **SignalR:** ASP.NET ki library jo **WebSocket** use karti hai. Is ki madad se server browser ko khud data "push" kar sakta hai. Page refresh karne ki zaroorat nahi parti.
- **Hub:** SignalR ka "radio station". Hamara hub `EmergencyMonitoringHub` hai, jo `/hubs/monitoring` par chalta hai.
- **JWT (token):** Login ke baad milne wala ek "digital pass". Har request ke saath jata hai: `Authorization: Bearer <token>`.
- **Device Key:** Machines (AI service, IoT gateway, simulator) ka password. Header: `X-Device-Key`.
- **EF Core:** C# classes ko SQL tables se jorne wala tool (ORM). SQL khud likhne ki zaroorat kam parti hai.
- **Migration:** Database ka "naqsha (blueprint)" jo code se banta hai aur SQL Server mein tables bana deta hai.
- **DTO:** Data ka woh "packet" jo API bahar bhejti ya andar leti hai. Database entity seedha bahar nahi bheji jaati.
- **Canvas:** HTML5 ka drawing board. Is par hum bounding boxes (chokor dabbe) banate hain.

### 1.4 End-to-end flow — AI ka data screen tak kaise pohanchta hai

```
 [External AI service]  (camera frame dekh kar JSON banati hai)
        │  JSON: bounding boxes, danger zones, fireSeverity
        │  (a) SignalR: hub method  PublishDetection(payload)      ← header X-Device-Key
        │  (b) ya REST:  POST /api/detections                      ← header X-Device-Key
        ▼
 [EmergencyMonitoringHub.cs / DetectionsController.cs]
        ▼
 [DetectionService.ProcessAsync]
   1. DetectionPayloadValidator.Validate()   → ghalat JSON = 400 error
   2. RealtimeNotifier.DetectionAsync()      → SignalR "detection" event SAB browsers ko
   3. Agar aag ya khatra hai → AlertEngine.EvaluateDetectionAsync()
        ▼
 [Angular: SignalRService]  connection.on('detection') → detection$ stream
        ▼
 [VideoFeedComponent] → OverlayRenderer.update(frame)
        ▼
 [OverlayLoopService]  requestAnimationFrame (≈60 dafa/second)
        → canvas par boxes, labels, danger zone, heatmap draw
```

**Bina refresh ke kaise?** Browser aur server ke darmiyan SignalR ka connection hamesha khula rehta hai. Server naya frame milte hi `Clients.All.SendAsync("detection", frame)` chalata hai. Angular ka `SignalRService` usay pakar leta hai, aur canvas har animation frame par dobara draw hota hai.

**Boxes ke rang (spec ke mutabiq):**

| Cheez | Rang | Kahan set hai |
|---|---|---|
| Fire | 🔴 Laal (`#ef4444`), pulse karta hai | `overlay-renderer.ts` → `OVERLAY_COLORS.fire` |
| Human (mehfooz) | 🟢 Hara (`#22c55e`) | `OVERLAY_COLORS.human` |
| Human danger zone ke andar (`atRisk: true`) | 🟠 Amber, **pulse** karta hai, label "IN DANGER" | `OVERLAY_COLORS.humanAtRisk` |
| Smoke | Grey | `OVERLAY_COLORS.smoke` |
| Danger zone polygon | Laal/amber dashed line, dheere dheere chalti hai | `drawZone()` |
| Heatmap | Peela → laal glow | `buildHeatmap()` |

**Video letterbox alignment:** Video `object-fit: contain` se dikhai jati hai, is liye kabhi upar/neeche kaali pattiyan aati hain. `contentRect()` function hisaab lagata hai ke asal video kis hisse mein hai, taake box theek insaan ke upar bane. Iska unit test `overlay-renderer.spec.ts` mein hai.

**Smooth movement:** Har object ka `trackId` hota hai (jaise `p1`). Naya frame aaye to box seedha jump nahi karta, "glide" karta hai (easing). AI 1.5 second tak data na bheje to tile par "NO AI DATA" aata hai. 4.5 second baad boxes gayab ho jate hain.

### 1.5 Danger alert kaise trigger hota hai (Alert Engine)

File: `backend/Sentinel.Api/Services/AlertEngine.cs`

**Step 1 — Severity (khatre ka darja):** `ClassifyDetection(frame)` yeh taye karta hai:

| Halat | Severity |
|---|---|
| Na aag, na koi danger mein | Kuch nahi (incident nahi banta) |
| Sirf aag | AI ki `fireSeverity` (na di ho to `Medium`) |
| Aag + insaan nazar aa rahe | Kam az kam `High` |
| Danger mein insaan 1–2 (aag ke baghair) | `High` |
| Danger mein insaan 3 ya zyada | `Critical` |
| **Aag + koi insaan danger zone mein** | **`Critical`** |

**Step 2 — Emergency rule (spec wala auto-trigger):**

```
Emergency = DangerCount > 0   YA   fireSeverity == "Critical"   (YA incident ki severity Critical ho)
```

Emergency ho to do kaam **khud ba khud** hote hain:
1. **SMS + Email + Push** notification queue mein daal di jati hai (`NotificationDispatcher.Enqueue`).
2. Us zone ke **Auto mode** wale sprinklers ON ho jate hain (`SprinklerService.AutoActivateZoneAsync`).

**Step 3 — Duplicate se bachao:**

| Mechanism | Kya karta hai | Setting |
|---|---|---|
| **Merge window** | Same camera/sensor ka khula incident 90 second ke andar dobara trigger ho to naya nahi banta, wahi update hota hai | `Alerts:IncidentMergeWindowSeconds = 90` |
| **Escalation** | Severity sirf **barhti** hai (High → Critical), kabhi kam nahi hoti. Is liye `Severity` hi "peak severity" hai | — |
| **Vision throttle** | Ek camera 5 frame/second bhejta hai, magar database har 2 second mein sirf ek dafa check hota hai (jab tak severity na barhe) | `Alerts:VisionEvaluationIntervalSeconds = 2` |
| **Cooldown** | Same source + zone + severity par 120 second tak dobara SMS nahi jata | `Alerts:CooldownSeconds = 120` |
| **Lock** | Do frames aik saath aayein to bhi duplicate incident na bane | `IncidentLocks` class |

### 1.6 Sensors (Smoke / Temperature) ka logic

File: `Services/SensorRules.cs`

| State | Shart (misaal: Temperature threshold 57°C) | Kya hota hai |
|---|---|---|
| `Normal` | 45.6°C se kam | Kuch nahi |
| `Warning` | ≥ threshold × 0.8 (45.6°C) | Screen par peela rang |
| `Alarm` | ≥ threshold (57°C) | `High` incident + zone ke Auto sprinklers ON |
| `Critical` | ≥ threshold × 1.25 (71.25°C). Smoke ke liye × 1.5 (450 ppm) | `Critical` incident + **emergency notifications** + sprinklers |

### 1.7 Smart Sprinklers kaise chalte hain

- Har sprinkler ka **Zone** hota hai (Zone A/B/C).
- **Mode = Auto:** Alert engine khud ON kar sakta hai.
- **Mode = Manual:** Sirf operator (dashboard ya API) ON/OFF kar sakta hai. Engine hath nahi lagata.
- **Master switch:** Saare sprinklers (Manual wale bhi) ek click mein ON/OFF. Pehle confirm dialog aata hai.
- Sprinkler khud OFF **nahi** hota. Aag bujhne ke baad operator OFF karta hai (asal zindagi mein bhi aisa hi hota hai).
- Simulator har 3 second mein sprinklers ki halat parhta hai. Jis zone mein sprinkler ON ho, wahan simulated aag kam hoti jati hai aur bujh jati hai.

### 1.8 Notifications kaise jati hain

File: `Services/Notifications/`

```
AlertEngine ──Enqueue──► NotificationDispatcher (queue, cooldown)
                              │  (turant return — API slow nahi hoti)
                              ▼
                     NotificationWorker (BackgroundService, alag thread)
                              ├─► TwilioSmsChannel   → SMS
                              ├─► EmailChannel       → Email (MailKit/SMTP)
                              └─► WebPushChannel     → Browser push
                              ▼
                     SignalR "notification" event → Dashboard ke Live Event Log mein result
```

- **Background mein kyun?** Twilio ya SMTP ko 2–5 second lag sakte hain. Agar yeh kaam request ke andar hota to live video overlay atak jata.
- **Configure na ho to?** Channel "skipped (not configured)" likh deta hai. Crash nahi hota.
- **Email "console mode":** `Notifications__Email__SmtpHost` khali ho magar Recipients diye hon, to email asal mein nahi jata. Uska poora text **API ki terminal window** mein print ho jata hai. Demo ke liye yeh bohat kaam ka hai.

SMS ka misaal text:
```
[Sentinel HQ] CRITICAL - FIRE — HUMANS IN DANGER
Location: Zone B (Warehouse Floor)
Humans in area: 2 | Humans in danger: 1
Fire severity: High
...
Incident #12 at 2026-09-30 15:18:30 UTC
http://localhost:4200/
```

---

## 2. Architecture & File Structure

### 2.1 Layers (tabaqay)

```
Angular Component (HTML button click)
      ↓
Angular Service (MonitoringApiService / SignalRService)
      ↓  HttpClient (+ authInterceptor: JWT laga deta hai)
ASP.NET Controller          ← sirf request lena/dena (patla)
      ↓
Service (business logic)    ← asal faisle yahan (AlertEngine, SensorService…)
      ↓
EF Core (SentinelDbContext)
      ↓
SQL Server (SentinelDb)
```

**Controller aur Service ka farq (asaan misaal):** Controller restaurant ka **waiter** hai: order leta hai aur khana laa kar deta hai. Service **chef** hai: asal khana (business logic) wahi banata hai. Waiter kitchen mein khana nahi pakata.

**Interface kyun?** `ISensorService` ek "wada (contract)" hai ke yeh methods maujood honge. Controller sirf interface jaanta hai. Kal ko service badalni ho to controller ko chherna nahi parta. Tests mein fake service lagana bhi aasan ho jata hai.

**Dependency Injection (DI):** `Program.cs` mein `builder.Services.AddScoped<ISensorService, SensorService>()` likha hai. Iska matlab: "jab koi `ISensorService` maange, to `SensorService` bana kar do". Controller ke constructor mein `ISensorService sensors` likhne se ASP.NET khud object de deta hai.

### 2.2 Folder structure

```
Project/
├── .env.example            ← saari settings ka namuna (commit hota hai)
├── .env                    ← asal secrets (KABHI commit na karein)
├── README.md
├── docs/
│   └── PROJECT_GUIDE_ROMAN_URDU.md   ← yeh guide
├── backend/
│   ├── Sentinel.slnx                 ← .NET solution (teeno projects)
│   ├── Sentinel.Api/                 ← ASP.NET Core Web API
│   ├── Sentinel.Simulator/           ← IoT + AI mock data bhejne wala console app
│   └── Sentinel.Api.Tests/           ← xUnit tests
└── frontend/                         ← Angular app
    ├── angular.json · package.json · .postcssrc.json (Tailwind)
    ├── public/sw.js                  ← Web Push service worker
    └── src/ (app/, environments/, styles.css)
```

### 2.3 Backend — har file ki zimmedari

| File | Zimmedari |
|---|---|
| `Program.cs` | App ka "switchboard": `.env` load, DI registrations, JWT + Device-key auth, CORS, SignalR, Swagger, rate-limit, middleware ki tarteeb, startup seeding, `generate-vapid-keys` command |
| `appsettings.json` | Default settings (connection string, Alerts, Notifications). **Is mein koi secret nahi** |
| `appsettings.Development.json` | Development mein demo data seed ON (`Seed:DemoData = true`) |
| `Properties/launchSettings.json` | Port: `http://localhost:5291` |
| **Configuration/** `Options.cs` | Settings ki C# classes: `AlertOptions`, `DeviceOptions`, `JwtOptions`, `SeedOptions`, `NotificationOptions` |
| **Configuration/** `DotEnv.cs` | `.env` file parh kar environment variables bana deta hai |
| **Models/** `CameraFeed.cs` | Camera table: `Id, Name, FeedType, StreamUrl, StreamFormat, Status, LocationZone` |
| **Models/** `SensorNode.cs` | Sensor table (`Id, Type, CurrentValue, Threshold, LocationZone`) + `SensorReading` (history) |
| **Models/** `SmartSprinkler.cs` | Sprinkler: `Id, Zone, IsActive, Mode, LastTriggeredAt` |
| **Models/** `IncidentAlert.cs` | Incident: `Id, Timestamp, CameraId, Severity, FireDetected, HumanCount, DangerCount, IsResolved` + zone, source, message |
| **Models/** `AppUser.cs` | Login users + `WebPushSubscription` |
| **Models/** `Enums.cs` | `Severity (Low<Medium<High<Critical)`, `FeedType`, `SprinklerMode`, waghera |
| **Data/** `SentinelDbContext.cs` | EF Core: tables, keys, indexes, foreign keys, enum→text, UTC dates |
| **Data/** `DbSeeder.cs` | Pehli dafa admin user + demo cameras/sensors/sprinklers |
| **DTOs/** `*.cs` | Request/Response packets (`LoginRequest`, `SensorReadingRequest`, `DetectionPayloadDto`…) + `Mapping.cs` (entity → DTO) |
| **Validators/** `DetectionPayloadValidator.cs` | AI JSON ki jaanch (bbox 4 numbers, heatmap size, waghera). REST aur SignalR dono yahi use karte hain |
| **Hubs/** `EmergencyMonitoringHub.cs` | SignalR hub `/hubs/monitoring`: connect par snapshot, `PublishDetection`, `PublishTelemetry` (device only), rate-limit 60 msg/sec |
| **Hubs/** `HubEvents.cs` | Event names: `snapshot, detection, telemetry, sprinkler, incident, notification, activity` |
| **Controllers/** `AuthController.cs` | `POST /api/auth/login`, `GET /api/auth/me` |
| **Controllers/** `DashboardController.cs` | `GET /api/dashboard/snapshot` |
| **Controllers/** `CamerasController.cs` | Camera list + MJPEG stream relay |
| **Controllers/** `IoTController.cs` | Sensor readings (simulate), telemetry batch, sprinkler toggle, master switch |
| **Controllers/** `DetectionsController.cs` | `POST /api/detections` (REST se AI frame) |
| **Controllers/** `IncidentsController.cs` | List, resolve, manual emergency dispatch |
| **Controllers/** `PushController.cs` | Web Push subscribe/unsubscribe |
| **Services/** `AlertEngine.cs` | 🧠 Severity, incident merge/escalate, emergency trigger |
| **Services/** `SensorService.cs` | Reading save + history + broadcast + alert engine ko dena |
| **Services/** `SprinklerService.cs` | ON/OFF, Auto/Manual, master, auto-activation |
| **Services/** `IncidentService.cs` | Incident list/resolve/dispatch |
| **Services/** `DetectionService.cs` | Validate → broadcast → evaluate |
| **Services/** `DashboardService.cs` | Poori screen ka data ek call mein (snapshot) |
| **Services/** `AuthServices.cs` | `TokenService` (JWT banana), `AuthService` (password check), `PushSubscriptionService` |
| **Services/** `RealtimeNotifier.cs` | SignalR ke zariye sab browsers ko events bhejna |
| **Services/Notifications/** | `NotificationDispatcher` (queue + cooldown), `NotificationWorker` (background), `Channels.cs` (Twilio SMS, Email, WebPush), `AlertMessage` (message ka text) |
| **Security/** | `DeviceKeyAuthenticationHandler` (X-Device-Key check), `AuthPolicies` (Operator / Device / OperatorOrDevice) |
| **Middleware/** | `ExceptionHandlingMiddleware`: har error ko saaf JSON (ProblemDetails) mein badalta hai. 400/401/404/500 |
| **Migrations/** | `InitialCreate`: SQL tables banane ki script (EF ne generate ki) |

### 2.4 Database tables (SQL Server `SentinelDb`)

```
CameraFeeds (Id PK "cam-warehouse")──┐ 1
                                     │         *
SensorNodes (Id PK "temp-b1") ──┬────┼──► IncidentAlerts (Id int PK)
                                │ 1  │        CameraId FK (SET NULL), SensorId FK (SET NULL)
                                │    │
                                └──* SensorReadings (Id, SensorId FK CASCADE, Value, RecordedAt)
SmartSprinklers (Id PK "spr-b1", Zone, IsActive, Mode)
Users (Id, Email UNIQUE, PasswordHash, Role) ──1──* PushSubscriptions (Endpoint UNIQUE)
```

- Enums text ki shakal mein save hote hain, jaise `"Critical"`, `"Auto"`. SQL mein parhna aasan rehta hai.
- Saare waqt (time) **UTC** mein save hote hain. Browser unhein apne local time mein dikhata hai.
- `SensorReadings` table mein 48 ghante se purani history khud delete hoti rehti hai.
- Camera delete ho jaye to incidents delete nahi hote, sirf `CameraId = NULL` ho jata hai.

### 2.5 Frontend — har file ki zimmedari

| File | Zimmedari |
|---|---|
| `app.config.ts` | Router + HttpClient + interceptors register |
| `app.routes.ts` | `/login` (guestGuard), `/` (authGuard) → Shell → Dashboard |
| `core/services/auth.service.ts` | Login, logout, token localStorage mein |
| `core/services/signalr.service.ts` | 📡 **SignalRService**: hub connection, auto-reconnect, saari live state (signals), `detection$` stream |
| `core/services/monitoring-api.service.ts` | Saari REST calls (HttpClient) |
| `core/services/push-notification.service.ts` | Browser push on/off |
| `core/services/ui.service.ts` | `ToastService` (popup), `ConfirmService` (dialog), `AlarmService` (awaaz) |
| `core/guards/auth.guard.ts` | Login ke baghair dashboard nahi khulta |
| `core/interceptors/auth.interceptor.ts` | Har API call par `Authorization: Bearer` lagata hai. 401 aaye to logout |
| `features/login/` | Login form (Reactive Form, validation) |
| `features/shell/` | Top bar: status pill, Live/Offline, clock, mute, Enable Alerts, Emergency Dispatch, Logout |
| `features/dashboard/dashboard.component.ts` | Screen ka layout |
| `features/dashboard/video-wall/` | Saare cameras ka grid, CCTV/Drone filter, Heatmap toggle, click = bara |
| `features/dashboard/video-feed/video-feed.component.*` | 🎥 **VideoFeedComponent**: player (MJPEG/HLS/MP4/Simulated) + canvas |
| `features/dashboard/video-feed/overlay-renderer.ts` | 🎨 Canvas drawing engine + ek shared `requestAnimationFrame` loop |
| `features/dashboard/sensor-panel/` | 🌡️ **SensorPanelComponent**: gauges, sparkline, sprinkler switches, Auto/Manual, Master |
| `features/dashboard/alert-logs/` | 📋 **AlertLogsComponent**: live incidents table + Resolve |
| `features/dashboard/activity-feed/` | Live Event Log (SMS gaya, sprinkler on hua, waghera) |
| `features/dashboard/kpi-strip/` | 4 numbers: Open Incidents, Humans Tracked, Peak Temp, Peak Smoke |
| `features/dashboard/dispatch-dialog/` | Emergency Dispatch form (Reactive Form) |
| `shared/models/api.models.ts` | Backend DTOs ki TypeScript interfaces |
| `shared/utils/format.ts` | Severity rank, system level, "5m ago", error message |
| `environments/*.ts` | API ka address (dev: `http://localhost:5291`) |
| `styles.css` | Tailwind + dark command-center theme |

**Button click ka flow (misaal: sprinkler switch):**
```
User switch dabata hai
 → sensor-panel.component.html (change)="toggle(s, $event)"
 → SensorPanelComponent.toggle()
 → MonitoringApiService.toggleSprinkler()  → HttpClient POST /api/iot/sprinklers/spr-b1/toggle
 → authInterceptor JWT lagata hai
 → IoTController.Toggle() → SprinklerService.ToggleAsync() → EF Core → SQL UPDATE SmartSprinklers
 → RealtimeNotifier "sprinkler" event → SAB open dashboards update
 → response → SignalRService.upsertSprinkler() → switch ON dikhta hai
```

### 2.6 Python version se kya badla

| Pehle (Django) | Ab (.NET + Angular) | Wajah |
|---|---|---|
| Django Channels `/ws/live-stream/` | SignalR `/hubs/monitoring` | Spec |
| Vanilla JS dashboard | Angular + Tailwind | Spec |
| SQLite | SQL Server | Spec. Is se SQLite ka "database is locked" masla bhi khatam ho gaya |
| Django admin login | JWT login page | Angular SPA ke liye |
| `fire_severity` 0..1 number | `fireSeverity` level: `Low/Medium/High/Critical` | Spec: "fire severity levels" |
| snake_case JSON | camelCase JSON | .NET ka default |
| Notifications har Medium+ incident par | Sirf **emergency** (DangerCount>0 / Critical) + manual dispatch par | Spec ka auto-trigger rule |
| Python simulator | C# `Sentinel.Simulator` | Ek hi stack |
| Docker files | Abhi nahi hain | Not verified. Dekhein section 6 |

---

## 3. Step-by-Step Chalane Ka Tariqa (Execution Guide)

### 3.1 Prerequisites (pehle yeh install hon)

| Tool | Version | Check command |
|---|---|---|
| .NET SDK | 8.0 ya naya (is machine par 8.0.425 aur 10.0.400 hain) | `dotnet --list-sdks` |
| EF Core tool | koi bhi naya | `dotnet ef --version` · install: `dotnet tool install --global dotnet-ef` |
| Node.js | 20.19+ / 22 / 24 | `node --version` |
| Angular CLI | 22 | `ng version` · install: `npm install -g @angular/cli` |
| SQL Server | 2019/2022 (Developer/Express) ya LocalDB | `sqlcmd -S localhost -E -C -Q "SELECT @@VERSION"` |

### 3.2 Settings file (`.env`) banayein

```powershell
cd C:\Users\m_fahad\Desktop\Project
Copy-Item .env.example .env
notepad .env
```

Kam az kam yeh teen cheezein bharein:

| Key | Kya likhein | Misaal |
|---|---|---|
| `Jwt__SigningKey` | 32+ characters ka random text | PowerShell: `[Convert]::ToBase64String((1..48 \| % {Get-Random -Max 256}))` |
| `Device__ApiKey` | Simulator/AI ka password | `dev-mykey-123456` |
| `Seed__AdminPassword` | Pehle admin ka password | `Sentinel@123` |

Optional: email ka demo dekhna ho to `Notifications__Email__Recipients=ops@sentinel.local` likhein. SMTP host khali chhor dein, email API terminal mein print hoga.

> ⚠️ `.env` git mein commit **na** karein. Repo mein `.gitignore` nahi hai, is liye `git add` se pehle `git status` zaroor dekhein. Local ignore ke liye rules `.git/info/exclude` mein rakhein (yeh file repo mein nahi jati).
> SQL Server default instance ki bajaye LocalDB use karna ho to `ConnectionStrings__Sentinel` mein `Server=(localdb)\MSSQLLocalDB;...` likhein.

### 3.3 Backend chalayein (Terminal 1)

```powershell
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet restore                                       # NuGet packages download
dotnet build                                         # compile — "Build succeeded" aana chahiye
dotnet ef database update --project Sentinel.Api     # SQL Server mein SentinelDb + tables banenge
dotnet run --project Sentinel.Api --launch-profile http
```

Kamyab hone par terminal mein yeh nazar aayega:
```
Created admin account admin@sentinel.local
Seeded demo data: 4 cameras, 6 sensors, 5 sprinklers
Now listening on: http://localhost:5291
```
- Swagger: **http://localhost:5291/swagger**
- Health check: **http://localhost:5291/health** → `{"status":"ok"}`

> **Migration ka matlab:** `dotnet ef database update` "InitialCreate" migration chalata hai. SQL Server mein `CameraFeeds, SensorNodes, SensorReadings, SmartSprinklers, IncidentAlerts, Users, PushSubscriptions` tables aur `__EFMigrationsHistory` ban jate hain. Model badlein to: `dotnet ef migrations add <Naam> --project Sentinel.Api` aur phir `database update`.

### 3.4 Frontend chalayein (Terminal 2)

```powershell
cd C:\Users\m_fahad\Desktop\Project\frontend
npm install        # pehli dafa (node_modules download)
ng serve           # http://localhost:4200
```

Browser mein **http://localhost:4200** kholein. Login karein: `admin@sentinel.local` / `.env` wala `Seed__AdminPassword`.

Top bar mein **"● Live"** (hara) nazar aaye to SignalR connect ho gaya hai. 4 camera tiles, 6 sensors aur 5 sprinklers dikhne chahiyein.

### 3.5 Simulator chalayein (Terminal 3) — fire ka demo

```powershell
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet run --project Sentinel.Simulator -- --device-key <.env wala Device__ApiKey> --ignite "Zone B"
```

| Option | Matlab | Default |
|---|---|---|
| `--device-key` | API ka `Device__ApiKey` (zaroori) | — |
| `--ignite "Zone B"` | Foran us zone mein aag lagao (kai dafa de sakte hain) | — |
| `--fps 5` | Har camera se har second kitne AI frames | 5 |
| `--telemetry-interval 2` | Sensor readings kitne second baad | 2 |
| `--fire-probability 0.004` | Khud-ba-khud aag lagne ka imkaan (0 = kabhi nahi) | 0.004 |
| `--base-url` | API ka address | `http://localhost:5291` |

**Simulator kya karta hai:**
- SignalR se `PublishDetection` bhejta hai: har camera ke 1–4 chalte phirte insaan, aag ka box, smoke, danger zone aur heatmap.
- Har 2 second baad `POST /api/iot/telemetry` bhejta hai: temperature = `22 + aag×75`, smoke = `15 + aag×620` (thora random noise ke saath).
- Har 3 second baad `GET /api/iot/sprinklers` parhta hai. Jis zone mein sprinkler ON ho, wahan aag bujhne lagti hai.

**Taqreeban timeline** (Zone B ke sprinklers Manual hon, yaani aag barhti rahe):

| Waqt | Kya hota hai |
|---|---|
| 0 s | Aag lagti hai (fireSeverity `Low`). Insaan paas hon to foran `Critical` |
| ~5 s | fireSeverity `Medium`, temperature Warning (≥45.6°C) |
| ~11 s | Temperature ≥57°C aur smoke ≥300 ppm → sensor `Alarm` → `High` incident |
| ~15 s | fireSeverity `High` |
| ~20–22 s | Temp ≥71.25°C / smoke ≥450 ppm → `Critical` → SMS/Email/Push |
| ~28 s | fireSeverity `Critical` |

Sprinklers **Auto** mode mein hon (default) to emergency hote hi woh ON ho jate hain. Aag lagbhag 10 second mein bujh jati hai (smoke test mein 11 second lage). Simulator log mein `Sprinklers suppressing fire in Zone B` → `Fire in Zone B extinguished` aata hai.

> 💡 Poori escalation dekhni ho: dashboard par `spr-b1` aur `spr-b2` ko **Manual** kar dein, phir `--ignite "Zone B"` chalayein.

### 3.6 Tests

```powershell
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet test                         # 25 tests: rules, validator, API, SignalR (SQLite in-memory)

cd C:\Users\m_fahad\Desktop\Project\frontend
ng test --watch=false               # 10 Vitest tests (geometry, formatting)
ng build                            # production build → frontend/dist/
```

---

## 4. Testing Flow — Swagger / Postman se Mock AI Data

### 4.1 Swagger mein login (token lena)

1. **http://localhost:5291/swagger** kholein.
2. `POST /api/auth/login` → **Try it out** → body:
   ```json
   { "email": "admin@sentinel.local", "password": "Sentinel@123" }
   ```
3. Response mein `"token": "eyJ..."` aayega. Usay copy karein.
4. Upar **Authorize 🔒** dabayein → **Bearer** box mein sirf token paste karein (lafz "Bearer" khud lag jata hai) → Authorize.
   - Device ki tarah test karna ho to **DeviceKey** box mein `.env` wala `Device__ApiKey` daalein.
5. Saath hi **http://localhost:4200** dashboard doosri window mein khula rakhein.

### 4.2 Test 1 — Mock AI payload: aag + insaan danger mein

`POST /api/detections`:
```json
{
  "cameraId": "cam-warehouse",
  "frameWidth": 1280,
  "frameHeight": 720,
  "normalized": false,
  "fireSeverity": "High",
  "detections": [
    { "label": "fire",   "category": "Fire",  "confidence": 0.93, "bbox": [520, 300, 180, 200], "trackId": "F1" },
    { "label": "person", "category": "Human", "confidence": 0.95, "bbox": [760, 280, 60, 150],  "trackId": "p1", "atRisk": true },
    { "label": "person", "category": "Human", "confidence": 0.91, "bbox": [1050, 300, 55, 140], "trackId": "p2" }
  ],
  "dangerZones": [
    { "label": "danger zone", "level": "Critical", "polygon": [[450,250],[850,250],[850,560],[450,560]] }
  ]
}
```

**Expected response — `202 Accepted`:**
```json
{ "accepted": true, "cameraId": "cam-warehouse", "fireDetected": true, "humanCount": 2, "dangerCount": 1, "incidentId": 1 }
```

**Dashboard par foran (bina refresh):**
- "Warehouse Floor" tile par 🔴 fire box, 🟠 pulse karta "IN DANGER · PERSON" box, 🟢 doosra person aur dashed laal danger zone.
- Top pill: **CRITICAL ALERT · 1 OPEN**, poori screen ke gird laal glow, alarm ki awaaz (agar pehle screen par ek dafa click kiya ho).
- Alert Logs mein naya **CRITICAL** row, aur toast "CRITICAL — Incident #1".
- Zone B ke **Auto** sprinklers (`spr-b1`, `spr-b2`) khud ON (neela), kyunke `DangerCount > 0` = emergency.
- Live Event Log: `SMS skipped (not configured)`, `EMAIL … console mode`, `PUSH skipped`.

> ⏱️ **Note:** Ek akela POST sirf ek frame hai. Boxes 1.5 second baad "NO AI DATA" ho jate hain aur 4.5 second mein gayab. Mustaqil boxes dekhne ke liye simulator chalayein (woh 5 frame/second bhejta hai).

### 4.3 Test 2 — Sensor value simulate karna

`POST /api/iot/sensors/temp-b1/readings`:
```json
{ "value": 60 }
```
- `200 OK`, response mein `"state": "Alarm"`. Dashboard par temperature gauge laal, `High` incident, Zone B ke Auto sprinklers ON.
- Phir `{ "value": 80 }` bhejein → `"state": "Critical"`. Incident **escalate** ho kar `Critical` ban jata hai aur notifications jati hain.
- Wapas normal: `{ "value": 22 }`.

Bulk (IoT gateway ki tarah) `POST /api/iot/telemetry`:
```json
{ "readings": [ { "sensorId": "smoke-a1", "value": 350 }, { "sensorId": "temp-a1", "value": 30 } ] }
```

### 4.4 Test 3 — Sprinkler toggle (Auto/Manual)

| Kaam | Request | Body | Expected |
|---|---|---|---|
| ON karna | `POST /api/iot/sprinklers/spr-a1/toggle` | `{ "isActive": true }` | 200, `isActive: true`, `lastTriggeredAt` set |
| Manual mode | same | `{ "mode": "Manual" }` | 200, `mode: "Manual"` |
| Ulta kar do (flip) | same | `{}` | 200 |
| Sab ON | `POST /api/iot/sprinklers/master` | `{ "isActive": true }` | 200, `changed: [...]` (sirf JWT operator) |
| Sirf ek zone | same | `{ "isActive": false, "zone": "Zone B" }` | 200 |

Har tabdeeli dashboard par foran nazar aati hai (SignalR `sprinkler` event).

### 4.5 Test 4 — Manual Emergency Dispatch & Resolve

- `POST /api/incidents/dispatch` → `{ "zone": "Zone A", "message": "Evacuate via east exit", "severity": "Critical", "activateSprinklers": true }` → **201 Created**.
- `POST /api/incidents/{id}/resolve` → 200, `isResolved: true`. Dashboard par row "Resolved" ho jati hai.
- Yahi kaam dashboard ke laal **Emergency Dispatch** button aur **Resolve** button se bhi hota hai.

### 4.6 Negative tests (ghalti wale)

| Test | Kaise | Expected |
|---|---|---|
| Token ke baghair | Authorize hata kar `GET /api/dashboard/snapshot` | **401** |
| Ghalat device key | `X-Device-Key: wrong` ke saath `GET /api/iot/sensors` | **401** |
| Ghalat password | login par `"password": "wrongpass"` | **401** `"Invalid email or password."` |
| Ghalat bbox | detection mein `"bbox": [1, 2]` | **400**, `errors["detections[0].bbox"]` |
| Ghalat heatmap | `"heatmap": {"rows":2,"cols":2,"values":[0.1]}` | **400** `heatmap.values` |
| Na-maloom sprinkler | `POST /api/iot/sprinklers/nope/toggle` | **404** |
| Na-maloom sensor | `POST /api/iot/sensors/xyz/readings` | **404** |
| Khali zone | dispatch mein `"zone": ""` | **400** |
| Device se dispatch | sirf DeviceKey ke saath `POST /api/incidents/dispatch` | **401** (sirf operators) |
| Login spam | 1 minute mein 11+ login | **429 Too Many Requests** |

### 4.7 Postman / PowerShell se

Postman headers: `Content-Type: application/json` aur ya to `Authorization: Bearer <token>` ya `X-Device-Key: <key>`.

PowerShell (bina Postman):
```powershell
$api = "http://localhost:5291"
$login = Invoke-RestMethod "$api/api/auth/login" -Method Post -ContentType "application/json" `
         -Body '{"email":"admin@sentinel.local","password":"Sentinel@123"}'
$h = @{ Authorization = "Bearer $($login.token)" }

Invoke-RestMethod "$api/api/iot/sensors/temp-b1/readings" -Method Post -Headers $h `
  -ContentType "application/json" -Body '{"value": 80}'

Invoke-RestMethod "$api/api/incidents?resolved=false" -Headers $h | Format-Table id, severity, zone, message
```

### 4.8 API ki mukammal list

| Method | URL | Kaun | Kaam |
|---|---|---|---|
| POST | `/api/auth/login` | Sab | Login → JWT |
| GET | `/api/auth/me` | Operator | Current user |
| GET | `/api/dashboard/snapshot` | Operator | Poori screen ka data |
| GET | `/api/cameras` · `/api/cameras/{id}` | Operator/Device | Cameras |
| GET | `/api/cameras/{id}/stream` | Operator | MJPEG relay (`?access_token=`) |
| GET | `/api/iot/sensors` · `/{id}` · `/{id}/history` | Operator/Device | Sensors + history |
| POST | `/api/iot/sensors/{id}/readings` | Operator/Device | Ek reading / simulation |
| POST | `/api/iot/telemetry` | Operator/Device | Bulk readings |
| GET | `/api/iot/sprinklers` | Operator/Device | Sprinklers |
| POST | `/api/iot/sprinklers/{id}/toggle` | Operator/Device | ON/OFF/Mode |
| POST | `/api/iot/sprinklers/master` | Operator | Sab ON/OFF |
| POST | `/api/detections` | Operator/Device | AI frame (REST) |
| GET | `/api/incidents` · `/{id}` | Operator | Incidents (`?resolved=&minSeverity=&limit=`) |
| POST | `/api/incidents/{id}/resolve` | Operator | Resolve |
| POST | `/api/incidents/dispatch` | Operator | Manual emergency |
| GET | `/api/push/vapid-public-key` | Operator | Push key |
| POST | `/api/push/subscribe` · `/api/push/unsubscribe` | Operator | Push on/off |
| GET | `/health` | Sab | Server zinda hai? |
| WS | `/hubs/monitoring` | Operator (`?access_token=`) / Device (`X-Device-Key`) | SignalR hub |

**SignalR hub methods (device → server):** `PublishDetection(payload)`, `PublishTelemetry({sensorId, value})`, `GetSnapshot()`.
**Server → browser events:** `snapshot`, `detection`, `telemetry`, `sprinkler`, `incident` (`{action, incident}`), `notification`, `activity`.

---

## 5. Common Troubleshooting

### 5.1 SignalR connect nahi ho raha (top bar "Offline" / "Reconnecting")

| Wajah | Kahan check karein | Hal |
|---|---|---|
| Backend chal hi nahi raha | Terminal 1, `http://localhost:5291/health` | `dotnet run --project Sentinel.Api --launch-profile http` |
| Ghalat address | `frontend/src/environments/environment.development.ts` → `hubUrl` | `http://localhost:5291/hubs/monitoring` hona chahiye |
| Token expire / backend restart hua aur JWT key random thi | API terminal mein `WARNING: Jwt__SigningKey not set` | `.env` mein `Jwt__SigningKey` set karein, dobara login karein |
| CORS (dekhein 5.2) | Browser console (F12) | `Cors__AllowedOrigins__0` |
| WebSocket kisi proxy/antivirus ne block kiya | F12 → Network → WS | Client khud LongPolling par chala jata hai. Proxy mein WebSocket allow karein |

**Simulator connect nahi ho raha:**
- `401` / `GET api/cameras returned 401`: `--device-key` aur `.env` ka `Device__ApiKey` bilkul same hon. API restart karein taake `.env` dobara parha jaye.
- `Cannot reach the API`: backend band hai ya `--base-url` ghalat hai.

### 5.2 CORS error

Browser console mein kuch aisa aaye: *"has been blocked by CORS policy"*

| Wajah | Hal |
|---|---|
| Angular 4200 ki bajaye kisi aur port par (jaise `ng serve --port 4300`) | `.env` mein `Cors__AllowedOrigins__0=http://localhost:4300` (ya `__1` se aur add karein), API restart |
| `127.0.0.1:4200` se khola, jab ke allow `localhost:4200` hai | Wahi address use karein jo allow hai, ya dono add karein |
| https vs http mismatch | Dono taraf same scheme |

Check karne ka tareeqa (204 aana chahiye):
```powershell
curl.exe -i -X OPTIONS http://localhost:5291/api/auth/login -H "Origin: http://localhost:4200" -H "Access-Control-Request-Method: POST"
```

### 5.3 Database / Migration masle

| Masla | Hal |
|---|---|
| `Cannot connect to SQL Server` | Services mein `MSSQLSERVER` Running hai? (`Get-Service MSSQLSERVER`). Connection string check karein. LocalDB: `Server=(localdb)\MSSQLLocalDB;...` |
| `Database has pending migration(s)` (API log) | `dotnet ef database update --project Sentinel.Api` |
| `dotnet ef` command nahi mila | `dotnet tool install --global dotnet-ef`, phir naya terminal |
| Certificate error | Connection string mein `TrustServerCertificate=True` ho |
| Dashboard khali (0 cameras) | Development mein chalayein (`--launch-profile http`), taake `Seed:DemoData=true` ho. Seeding sirf tab hoti hai jab tables khali hon |

### 5.4 Login nahi ho raha

- Admin **sirf pehli dafa** banta hai (jab `Users` table khali ho). Us waqt `.env` mein jo password tha, wahi chalega.
- Password khali tha to API ne ek random password terminal mein print kiya tha (`GENERATED password`).
- Reset karna ho (dev mein): `sqlcmd -S localhost -E -C -d SentinelDb -Q "DELETE FROM Users"`, phir API restart. Naya admin `.env` wale password se ban jayega.

### 5.5 Baqi

| Masla | Hal |
|---|---|
| Port 5291 / 4200 pehle se busy | Purana process band karein, ya `ng serve --port 4300` (CORS bhi update karein) |
| SMS nahi gaya | Twilio ki teenon settings + `Notifications__Sms__Recipients` (E.164 format `+92…`). Trial account par sirf verified numbers |
| Email nahi aaya | `SmtpHost` khali = console mode (API terminal dekhein). Gmail ke liye App Password aur port 587 |
| Push nahi aaya | `generate-vapid-keys` se keys `.env` mein daalein, **Enable Alerts** dabayein, browser permission "Allow". Sirf `localhost` ya HTTPS par chalta hai |
| Alarm ki awaaz nahi | Browser pehle ek click maangta hai (autoplay rule). Mute button check karein |
| Box ghalat jagah par | `normalized` flag aur `frameWidth/frameHeight` asal frame ke mutabiq hon. Code: `overlay-renderer.ts` → `contentRect()` / `toUnitBox()` |
| `npm warn allow-scripts` | Sirf warning hai, build par asar nahi |

Web Push keys:
```powershell
cd C:\Users\m_fahad\Desktop\Project\backend
dotnet run --project Sentinel.Api -- generate-vapid-keys
```

---

## 6. Kya Verify Hua, Kya Nahi

**Is machine par chala kar verify kiya gaya:**
- ✅ `dotnet build` (API, Simulator, Tests): kamyab.
- ✅ `dotnet ef migrations add InitialCreate` + `database update`: SQL Server 2022 par 7 tables ban gaye.
- ✅ `dotnet test`: 25/25 pass.
- ✅ `ng build` aur `ng test`: build kamyab, 10/10 tests pass.
- ✅ API: login, 401, Swagger, CORS preflight (204).
- ✅ Simulator → SignalR → incident → auto-sprinklers → email (console mode) → cooldown.
- ✅ Browser (headless Edge) mein poora flow chalaya:
  - login, "Live" connection, 4 tiles / 6 sensors / 5 sprinklers
  - canvas par boxes aur danger zones bane
  - CRITICAL status, alert logs update
  - sprinkler switch aur Emergency Dispatch form UI se chalaye
  - console mein koi error nahi.

**Not verified from current implementation:**
- ❌ Asal Twilio SMS, asal SMTP email aur asal Web Push delivery (credentials nahi the; sirf "skipped" / console mode test hua).
- ❌ Asal CCTV/drone MJPEG ya HLS stream (koi asli camera URL nahi tha; demo cameras "Simulated" hain).
- ❌ Production deployment (IIS / Linux / Docker / HTTPS certificate). Is project mein Docker files ab **nahi** hain.
