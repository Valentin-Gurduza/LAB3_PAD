# PAD Lab 3 — Data Warehouse Distribuit (C# / NoSQL / Gateway) — Design Spec

**Data:** 2026-10-28  
**Țintă:** Nota 10 + toate cele 5 bonusuri (8p bază + 5p bonus)  
**Tehnologii:** .NET 8 (ASP.NET Core), Ocelot + Polly, Consul, MongoDB Replica Sets, Mongo Express, Redis, Docker Compose, Kubernetes, Postman

---

## 1. Obiectiv și Cerințe Acoperite

Sistem distribuit de tip **Data Warehouse** pentru analiza vânzărilor globale, expus printr-un singur punct de intrare (**API Gateway**), fără autentificare (complet deschis pentru inspecție și demonstrare).

| Nivel | Cerință | Soluție aleasă |
|---|---|---|
| **Nota 5** | C# REST API + Data Warehouse | ASP.NET Core 8 (`Warehouse.Node`), domeniu vânzări globale (`SaleRecord` + agregări OLAP) |
| **Nota 5** | Bază de date NoSQL | **MongoDB** (Document Store — ideal pentru documente analitice JSON/XML și agregări) |
| **Nota 5** | Format date JSON sau XML | JSON implicit + XML activat nativ (ambele simultan pentru bonus) |
| **Nota 5** | Full CRUD **fără verbe în path** | Strict substantive: `/api/sales`, `/api/sales/{id}`, `/api/sales/reports` |
| **Nota 8** | Scalabilitate (3 API + 3 DB) | 3 instanțe `Warehouse.Node` (`api-1`, `api-2`, `api-3`), fiecare izolată pe propriul Shard MongoDB |
| **Nota 8** | Sharding + Sincronizare | Hash determinist FNV-1a pe `Id` (`Hash(Id) % 3`), rutare inter-nod și *scatter-gather* prin Consul |
| **Nota 8** | API Gateway + Round-Robin | **Ocelot** (`Warehouse.Gateway`) cu load balancer `RoundRobin` |
| **Nota 8** | Service Discovery + Health Check | **Consul** (`hashicorp/consul`), auto-înregistrare la startup + verificare `/health` la 5s |
| **Nota 8** | Caching cu expirare (TTL) | **Redis** conectat la Gateway, TTL = 30s pe `GET`, invalidare automată la `POST`/`PUT`/`DELETE` |
| **Nota 8** | UI deschis pentru baza de date | **Mongo Express** fără autentificare pe `:8081` (Shard 0), `:8082` (Shard 1), `:8083` (Shard 2) + Consul UI `:8500` |
| **Nota 8** | Pornire simultană + Script populare | `./scripts/start.sh` (Docker Compose + init Replica Sets) și `./scripts/seed.sh` (populare date) |
| **Nota 8** | Client Postman | Colecție Postman completă (`postman/PAD_Lab3.postman_collection.json`) |
| **Bonus 1** | Docker & Kubernetes | `Dockerfile`-uri multi-stage, `docker-compose.yml` complet și manifeste Kubernetes în `k8s/` |
| **Bonus 2** | Modificare și citire prin JSON **și** XML | Content Negotiation ASP.NET Core (`Accept` și `Content-Type`: `application/json` / `application/xml`) |
| **Bonus 3** | Prioritate pentru requesturi | Middleware în Gateway cu `PriorityQueue` bazat pe headerul `X-Priority: high \| normal \| low` |
| **Bonus 4** | Circuit Breaker la Gateway | **Polly** integrat în Ocelot (`ExceptionsAllowedBeforeBreaking: 2`, `DurationOfBreak: 10s`, `TimeoutValue: 2.5s`) |
| **Bonus 5** | Replicare DB (1 Master + 2 Backup) | Fiecare Shard este un **MongoDB Replica Set** cu 3 noduri (`rs0`, `rs1`, `rs2`) și alegere automată de Primary |

---

## 2. Arhitectura Sistemului și Structura Proiectului

```text
LAB3_PAD/
├── Warehouse.sln
├── src/
│   ├── Warehouse.Gateway/          # Ocelot + Consul + Polly + Redis Cache + PriorityQueue
│   │   ├── Program.cs
│   │   ├── ocelot.json
│   │   ├── ConsulServiceBuilder.cs # Rezolvă adresa reală a containerului din Consul Service.Address
│   │   ├── PriorityQueueMiddleware.cs
│   │   ├── RedisCacheMiddleware.cs
│   │   └── Dockerfile
│   └── Warehouse.Node/             # Microserviciu REST Data Warehouse (rulat în 3 instanțe)
│       ├── Program.cs
│       ├── Models/
│       │   ├── SaleRecord.cs       # Entitate compatibilă JSON + XmlSerializer + MongoDB BSON
│       │   └── SalesReport.cs      # Raport agregat Data Warehouse (fără Dictionary pt. compatibilitate XML)
│       ├── Services/
│       │   ├── ShardHelper.cs      # FNV-1a hash determinist: GetShard(id, shardCount)
│       │   ├── WarehouseRepository.cs
│       │   ├── ConsulRegistrationHostedService.cs
│       │   └── PeerShardClient.cs  # Rutare directă pe shard și scatter-gather între noduri
│       ├── Controllers/
│       │   └── SalesController.cs  # Rute CRUD + rapoarte fără verbe în URL
│       └── Dockerfile
├── docker-compose.yml              # Consul + Redis + 9 noduri Mongo (3x3) + init RS + 3 Mongo Express + 3 API + Gateway
├── scripts/
│   ├── init-mongo-rs.sh            # Inițializează rs0, rs1, rs2 cu electionTimeoutMillis=2000
│   ├── start.sh                    # Pornește toată infrastructura și așteaptă health checks
│   ├── seed.sh                     # Populează Data Warehouse-ul prin Gateway (JSON și XML)
│   └── demo-priority.sh            # Trimite un burst concurent low/normal/high pentru demonstrarea priorității
├── postman/
│   └── PAD_Lab3.postman_collection.json
└── k8s/
    ├── 01-infra.yaml               # Consul + Redis Deployments & Services
    ├── 02-mongo-shards.yaml        # StatefulSets rs0, rs1, rs2 + Job inițializare Replica Sets + Mongo Express
    ├── 03-warehouse-nodes.yaml     # Cele 3 Deployment-uri api-1 (shard 0), api-2 (shard 1), api-3 (shard 2)
    └── 04-gateway.yaml             # Ocelot Gateway Deployment + NodePort Service
```

---

## 3. Modelul de Date și Serializarea JSON + XML

Pentru ca `System.Xml.Serialization.XmlSerializer` și `System.Text.Json` să funcționeze identic fără erori, toate modelele au constructor public fără parametri și folosesc `List<T>` în loc de `Dictionary<K,V>`.

### `SaleRecord`
- `Id` (`string`): Identificator unic (ex. `sale-1001`; generat automat `sale-<8hex>` la `POST` dacă lipsește).
- `Region` (`string`): Regiunea comercială (`EU`, `US`, `APAC`).
- `Category` (`string`): Categoria produsului (`Electronics`, `Furniture`, `Software`, etc.).
- `Product` (`string`): Denumirea produsului.
- `Quantity` (`int`): Cantitatea vândută (`>= 1`).
- `UnitPrice` (`decimal`): Prețul unitar (`>= 0`).
- `TotalAmount` (`decimal`): Calculat automat `Quantity * UnitPrice`.
- `RecordedAt` (`DateTime`): Timestamp UTC.
- `ShardId` (`int`): Shard-ul proprietar (`0`, `1` sau `2`), calculat determinist din `Id`.

### `SalesReport` (Data Warehouse OLAP Summary)
- `TotalRecords` (`int`)
- `GrandTotalRevenue` (`decimal`)
- `ActiveShards` (`List<int>`)
- `ByRegion` (`List<GroupMetric>`: `Name`, `Count`, `TotalQuantity`, `TotalRevenue`)
- `ByCategory` (`List<GroupMetric>`: `Name`, `Count`, `TotalQuantity`, `TotalRevenue`)

---

## 4. Interfața REST API (Zero Verbe în Path)

Clientul (Postman) comunică exclusiv cu Gateway-ul pe `http://localhost:8000`:

| Metodă | Path | Descriere | Cod Răspuns |
|---|---|---|---|
| `GET` | `/api/sales` | Returnează toate înregistrările din toate shard-urile active (filtre opționale `?region=EU&category=Tech`) | `200 OK` |
| `GET` | `/api/sales/{id}` | Returnează înregistrarea după `id` din shard-ul responsabil | `200 OK` / `404 Not Found` |
| `POST` | `/api/sales` | Creează o înregistrare în shard-ul determinat de `Hash(Id) % 3` (JSON sau XML) | `201 Created` |
| `PUT` | `/api/sales/{id}` | Actualizează complet înregistrarea în shard-ul responsabil (JSON sau XML) | `200 OK` / `404 Not Found` |
| `DELETE` | `/api/sales/{id}` | Șterge înregistrarea din shard-ul responsabil | `204 No Content` / `404 Not Found` |
| `GET` | `/api/sales/reports` | Returnează raportul agregat Data Warehouse pe regiuni și categorii | `200 OK` |

### Headere Deschise pentru Diagnostic la Prezentare
Fiecare răspuns include headere vizibile în Postman:
- `X-Served-By`: Nodul API care a primit cererea de la Gateway (`api-1`, `api-2` sau `api-3`) — demonstrează **Round-Robin**.
- `X-Owner-Shard`: Shard-ul MongoDB (`0`, `1` sau `2`) unde este stocată înregistrarea — demonstrează **Sharding**.
- `X-Cache`: `HIT` sau `MISS` (plus `X-Cache-TTL` în secunde la `HIT`) — demonstrează **Redis Caching**.
- `X-Priority-Applied`: `high`, `normal` sau `low` (plus `X-Queue-Wait-Ms`) — demonstrează **Prioritatea**.
- `X-Degraded-Shards`: Prezent doar dacă unul din nodurile API este oprit intenționat în timpul unui `GET /api/sales` sau `GET /api/sales/reports`.

---

## 5. Fluxul Distribuit și Detaliile Mecanismelor

### 5.1 Service Discovery (Consul) & Health Checks
- La pornire, `ConsulRegistrationHostedService` înregistrează nodul în Consul cu:
  - `Name = "warehouse-node"`
  - `ID = NODE_ID` (`api-1`, `api-2`, `api-3`)
  - `Tags = ["shard:0"]` (respectiv `shard:1`, `shard:2`)
  - HTTP Health Check la `http://<container>:8080/health` la fiecare `5s` (timeout `2s`, deregistrare automată la eșec prelungit).
- Endpoint-ul `/health` verifică printr-un `ping` rapid că Replica Set-ul MongoDB al shard-ului are un nod activ.
- În `Warehouse.Gateway`, Ocelot folosește `CustomConsulServiceBuilder` (care returnează `entry.Service.Address` în loc de `entry.Node.Name`) cu `LoadBalancerOptions.Type = "RoundRobin"`.

### 5.2 Sharding & Sincronizare între Noduri
- `ShardHelper.GetShard(string id, int shardCount = 3)` aplică algoritmul determinist FNV-1a 32-bit (`uint`) pe șirul `id` (`(int)(hash % (uint)shardCount)`), garantând același rezultat în toate instanțele și la orice repornire (spre deosebire de `string.GetHashCode()` din .NET care este randomizat per proces!).
- **Operații pe o cheie (`GET/{id}`, `POST`, `PUT/{id}`, `DELETE/{id}`):**
  1. Nodul ales de Gateway prin Round-Robin calculează `ownerShard = GetShard(id, 3)`.
  2. Dacă `ownerShard == localShard` (sau cererea are deja headerul `X-Internal-Shard-Hop: true`), operația se execută direct în propriul MongoDB Replica Set.
  3. Dacă `ownerShard != localShard`, `PeerShardClient` interoghează Consul pentru nodul sănătos cu tag-ul `shard:<ownerShard>` și redirecționează cererea HTTP (păstrând body-ul și headerele `Accept`/`Content-Type` JSON/XML) cu headerul `X-Internal-Shard-Hop: true`. Dacă nodul proprietar este oprit, întoarce `503 Service Unavailable` cu mesaj clar că shard-ul respectiv este indisponibil.
- **Operații globale (`GET /api/sales`, `GET /api/sales/reports`):**
  1. Dacă cererea are `X-Internal-Shard-Hop: true`, nodul returnează strict datele din shard-ul local.
  2. Dacă cererea vine de la Gateway, nodul citește shard-ul local și interoghează în paralel (`Task.WhenAll`, timeout `2s`) celelalte shard-uri active din Consul, combinând rezultatele și calculând agregările Data Warehouse.

### 5.3 Redis Caching în Gateway
- `RedisCacheMiddleware` rulează în fața lui Ocelot în `Warehouse.Gateway`:
  - Pentru `GET /api/*` (fără headerul de test `X-Simulate-Fail-Node` și fără `Cache-Control: no-cache`):
    - Construiește cheia `cache:{path}{query}:fmt={(accept.Contains("xml") ? "xml" : "json")}`.
    - Dacă există în Redis, returnează instant răspunsul salvat cu `X-Cache: HIT` și `X-Cache-TTL`.
    - Dacă nu există (`MISS`), lasă Ocelot să execute cererea; dacă statusul este `200 OK`, salvează body-ul + `Content-Type` + headerele de shard/nod în Redis cu `TTL = 30 secunde` și returnează `X-Cache: MISS`.
  - Pentru `POST`, `PUT`, `DELETE` finalizate cu succes (`2xx`), șterge toate cheile `cache:/api/sales*` din Redis pentru coerență imediată.

### 5.4 Prioritate Requesturi în Gateway
- `PriorityQueueMiddleware` limitează numărul de cereri procesate simultan spre downstream (configurat implicit `MaxConcurrentRequests = 2` pentru demonstrare ușoară) și pune cererile concurente în așteptare într-un `PriorityQueue<TaskCompletionSource, (int Rank, long Sequence)>`:
  - `X-Priority: high` (sau `?priority=high`) → `Rank = 0`
  - `X-Priority: normal` (implicit) → `Rank = 1`
  - `X-Priority: low` → `Rank = 2`
- La eliberarea unui slot, coada extrage garantat cererea cu cea mai mare prioritate (și FIFO în cadrul aceleiași priorități prin `Sequence`).

### 5.5 Circuit Breaker (Polly în Ocelot)
- Configurat în `ocelot.json`:
  - `ExceptionsAllowedBeforeBreaking: 2`
  - `DurationOfBreak: 10000` (10 secunde)
  - `TimeoutValue: 2500` (2.5 secunde)
- Pentru demonstrare ușoară în fața profesorului fără a modifica codul: dacă trimiți headerul `X-Simulate-Fail-Node: api-1`, nodul `api-1` așteaptă `4000ms` (depășind timeout-ul de `2500ms`), ceea ce declanșează **Polly Circuit Breaker** pe acel nod și redirecționează traficul spre nodurile sănătoase.

### 5.6 Replicare Bază de Date (1 Master + 2 Replici per Shard)
- Fiecare shard are propriul Replica Set MongoDB de 3 noduri:
  - **Shard 0 (`rs0`):** `mongo-s0-1`, `mongo-s0-2`, `mongo-s0-3`
  - **Shard 1 (`rs1`):** `mongo-s1-1`, `mongo-s1-2`, `mongo-s1-3`
  - **Shard 2 (`rs2`):** `mongo-s2-1`, `mongo-s2-2`, `mongo-s2-3`
- Scriptul `scripts/init-mongo-rs.sh` configurează fiecare Replica Set cu `settings.electionTimeoutMillis = 2000`.
- Connection string-ul fiecărui API include toate cele 3 noduri ale shard-ului său (`mongodb://mongo-s0-1:27017,mongo-s0-2:27017,mongo-s0-3:27017/warehouse?replicaSet=rs0&readPreference=primaryPreferred`).
- Dacă nodul Primary cade (`docker stop mongo-s0-1`), cele 2 noduri Secondary votează un nou Primary în ~2–3 secunde, iar `api-1` și `mongo-express` continuă să citească și să scrie fără întrerupere.

---

## 6. Strategia de Testare și Verificare
1. **Build & Unit Self-Check:** Compilare fără avertismente pentru `Warehouse.sln` + verificarea deterministă a sharding-ului și serializării XML/JSON.
2. **Pornire Simultană & Seeding:** Rulare `./scripts/start.sh` urmată de `./scripts/seed.sh` și verificare că înregistrările sunt împărțite pe toate cele 3 shard-uri (`:8081`, `:8082`, `:8083`).
3. **CRUD JSON & XML:** Testare `POST`, `GET`, `PUT`, `DELETE` și `GET /api/sales/reports` atât cu `application/json`, cât și cu `application/xml`.
4. **Caching & Invalidare:** Două `GET` consecutive (`MISS` -> `HIT`), urmate de `POST` și `GET` (`MISS`).
5. **Prioritate & Circuit Breaker:** Rulare `./scripts/demo-priority.sh` și testare `X-Simulate-Fail-Node`.
6. **Failover API & Failover MongoDB Master:** Oprire `api-2` (Consul + Round-Robin continuă pe `api-1`/`api-3`) și oprire `mongo-s0-1` (alegere automată de nou Master în `rs0` fără pierdere de date).
7. **Validare Kubernetes:** Validare sintactică a manifestelor din `k8s/` cu `kubectl apply --dry-run=client -f k8s/`.
