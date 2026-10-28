# Lucrarea de Laborator Nr. 3 — PAD (Data Warehouse Distribuit în C#)

Sistem distribuit **Data Warehouse** pentru analiza vânzărilor globale, realizat în **C# (.NET 8)** cu **Ocelot API Gateway**, **Consul Service Discovery**, **MongoDB Sharding & Replica Sets (1 Master + 2 Replici)**, **Redis Cache cu TTL**, **Polly Circuit Breaker**, **Coadă cu Prioritate**, suport dublu **JSON + XML**, **Docker Compose** și **Kubernetes**.

---

## 1. Pornire Rapidă și Populare Bază de Date

```bash
# 1. Pornire simultană a întregului cluster (Gateway + 3 API + Consul + Redis + 9 noduri Mongo + 3 UI DB)
./scripts/start.sh

# 2. Populare Data Warehouse cu înregistrări (atât prin JSON cât și prin XML)
./scripts/seed.sh
```

### Interfețe Deschise (fără parolă / autentificare)
- **API Gateway (singurul punct cunoscut de Postman):** `http://localhost:8000`
- **Consul UI (Service Discovery & Health Checks):** `http://localhost:8500`
- **Mongo Express UI — Shard 0 (`rs0`):** `http://localhost:8081`
- **Mongo Express UI — Shard 1 (`rs1`):** `http://localhost:8082`
- **Mongo Express UI — Shard 2 (`rs2`):** `http://localhost:8083`
- **Colecție Postman:** importă fișierul `postman/PAD_Lab3.postman_collection.json` în Postman.

---

## 2. Rutele REST API (Full CRUD — ZERO verbe în path)

| Metodă | URL prin Gateway | Descriere |
|---|---|---|
| `GET` | `http://localhost:8000/api/sales` | Toate înregistrările din toate shard-urile active (`?region=EU&category=Software`) |
| `GET` | `http://localhost:8000/api/sales/{id}` | O înregistrare după `id` (rutată automat spre shard-ul proprietar) |
| `POST` | `http://localhost:8000/api/sales` | Adăugare înregistrare nouă (JSON sau XML) |
| `PUT` | `http://localhost:8000/api/sales/{id}` | Actualizare înregistrare existentă (JSON sau XML) |
| `DELETE` | `http://localhost:8000/api/sales/{id}` | Ștergere înregistrare |
| `GET` | `http://localhost:8000/api/sales/reports` | Raport agregat Data Warehouse (totaluri pe regiuni și categorii) |

---

## 3. Cum demonstrezi fiecare cerință și bonus în fața profesorului

### A. JSON + XML (Content Negotiation)
În Postman schimbă headerele `Accept` și `Content-Type` între `application/json` și `application/xml` pe oricare rută (`GET`, `POST`, `PUT`).

### B. Round-Robin + Redis Caching cu TTL (30 secunde)
1. Trimite în Postman `GET http://localhost:8000/api/sales` prima dată -> în tab-ul **Headers** vezi `X-Cache: MISS` și `X-Served-By: api-1`.
2. Trimite imediat a doua oară -> vezi `X-Cache: HIT` și `X-Cache-TTL: 28` (răspuns instant din Redis).
3. Trimite cu header `Cache-Control: no-cache` de 3 ori la rând -> vezi cum `X-Served-By` alternează `api-1` -> `api-2` -> `api-3` (**Round-Robin**).
4. Fă un `POST` sau `DELETE` -> cache-ul Redis se invalidează automat.

### C. Sharding & Sincronizare între bazele de date
- Deschide `http://localhost:8081`, `:8082` și `:8083` -> în baza `warehouse` -> colecția `sales`, datele sunt împărțite pe cele 3 shard-uri conform hash-ului determinist FNV-1a (`ShardHelper.GetShard(id, 3)`).
- Chiar dacă Ocelot trimite cererea la `api-1`, dacă înregistrarea aparține de `shard-2`, `api-1` o sincronizează/redirecționează intern la `api-3` (vezi headerele `X-Served-By`, `X-Forwarded-To`, `X-Owner-Shard`).

### D. Service Discovery & Health Check ("Off Check" când cade un API)
```bash
docker stop pad-api-2
```
- În ~5 secunde, pe `http://localhost:8500` (Consul UI), `api-2` apare picat și Ocelot trimite traficul doar spre `api-1` și `api-3`.
- Repornire: `docker start pad-api-2` (se reconectează automat).

### E. Bonus — Prioritate Requesturi (`X-Priority: high | normal | low`)
```bash
./scripts/demo-priority.sh
```
Trimite un burst concurent: cererile cu `X-Priority: high` sar în fața celor `normal` și `low` în `PriorityQueueMiddleware`.

### F. Bonus — Circuit Breaker (Polly în Ocelot)
Trimite de 3 ori la rând din Postman request-ul **12. Bonus - Test Circuit Breaker** (`X-Simulate-Fail-Node: *`, `X-Simulate-Delay-Ms: 4000`):
1. Primele **2 cereri** sunt tăiate de Ocelot la timeout-ul de **2.5s** (`TimeoutValue: 2500`).
2. După 2 eșecuri (`ExceptionsAllowedBeforeBreaking: 2`), Polly **deschide circuitul** timp de `10s` (`DurationOfBreak: 10000`) — a **3-a cerere** returnează instant `503` în câteva milisecunde (*fail-fast*), fără să mai aștepte timeout-ul! (Pentru redirecționarea traficului când cade un singur nod, vezi punctul **D** mai sus.)

### G. Bonus — Replicare Bază de Date (1 Master + 2 Backup per Shard)
Fiecare shard are 3 noduri MongoDB în Replica Set (`rs0`: `pad-mongo-s0-1` = Master inițial, `s0-2` și `s0-3` = Backup/Secondary).
```bash
# 1. Verifică cine este Master (PRIMARY) pe Shard 0:
docker exec pad-mongo-s0-1 mongosh --quiet --eval "rs.status().members.map(m => ({name: m.name, state: m.stateStr}))"

# 2. Oprește Master-ul Shard-ului 0:
docker stop pad-mongo-s0-1

# 3. După ~2-3 secunde verifică pe replica 2 că s-a ales automat un nou PRIMARY:
docker exec pad-mongo-s0-2 mongosh --quiet --eval "rs.status().members.map(m => ({name: m.name, state: m.stateStr}))"

# 4. API-ul și UI-ul pe :8081 continuă să funcționeze fără nicio eroare!
# Repornire fostul Master (revine automat în cluster):
docker start pad-mongo-s0-1
```

### H. Bonus — Kubernetes
Manifestele complete pentru întregul cluster sunt în directorul `k8s/`:
```bash
# Validare offline a manifestelor Kubernetes (fără cluster pornit):
kubectl kustomize k8s/ > /dev/null && echo "Manifeste K8s valide!"
# Pentru aplicare pe un cluster local (ex. kind / minikube):
# docker build -t warehouse-node:latest -f src/Warehouse.Node/Dockerfile .
# docker build -t warehouse-gateway:latest -f src/Warehouse.Gateway/Dockerfile .
# kubectl apply -f k8s/
```
