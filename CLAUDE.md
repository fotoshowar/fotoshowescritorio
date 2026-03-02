# FotoShow Escritorio — Contexto del Proyecto

## Qué es esto
Integración nativa de Windows para fotógrafos de FotoShow.
**No es una app con ventana** — es una shell extension que vive en el Explorer.

Flujo completo:
1. Fotógrafo clic derecho en carpeta/foto → "Agregar a FotoShow"
2. La app procesa localmente (thumbnail + embedding facial)
3. Sube SOLO metadata al backend (no la foto original)
4. Cuando alguien compra → notificación via WebSocket → sube la foto original automáticamente
5. Todo el management (galerías, precios, ventas) desde fotoshow.online

## Estructura del repo
```
FotoshowTray/           ← Background service (.NET 8 WinForms sin ventana)
  Program.cs            ← Entry point, single instance, forwarding de args
  TrayApp.cs            ← NotifyIcon, orquesta servicios
  PipeServer.cs         ← Named pipe "FotoshowTray" — recibe comandos del Explorer
  PhotoQueue.cs         ← Cola de procesamiento lazy (thumbnail + embedding)
  AiClient.cs           ← TCP client al ai_worker.exe (puerto 54321)
  BackendClient.cs      ← HTTP sync + WebSocket ventas + entrega foto
  LocalDb.cs            ← SQLite en %APPDATA%\Fotoshow\photos.db (WAL)
  Models/
    PhotoRecord.cs      ← Estado local de cada foto
    TrayConfig.cs       ← Config persistida en %APPDATA%\Fotoshow\config.json

FotoshowShell/          ← COM DLL overlay icons (.NET Framework 4.8 + SharpShell)
  OverlayIcons.cs       ← 3 handlers: pending(gris) synced(azul) sold(verde)

setup/
  setup.iss             ← InnoSetup installer

fotoshow con vista nueva/   ← WPF app anterior (referencia, no usar)
ai_worker.py                ← Worker IA (InsightFace) — compilar con ai_worker.spec
FotoshowEscritorio.sln      ← Solución principal (FotoshowTray + FotoshowShell)
```

## Stack
- **FotoshowTray**: .NET 8, WinForms (solo para tray, sin ventana), EF Core + SQLite, ImageSharp
- **FotoshowShell**: .NET Framework 4.8, SharpShell 2.7.2, System.Data.SQLite.Core
- **Installer**: InnoSetup 6+
- **AI Worker**: Python + InsightFace, compilado con Nuitka (`ai_worker.exe`)
- **Backend**: fotoshow.online (FastAPI en Vultr)

## Cómo compilar

### Prerequisitos Windows
- Visual Studio 2022 o VS Build Tools 2022
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- .NET Framework 4.8 Developer Pack
- InnoSetup 6 (para el installer)

### Build
```powershell
# Compilar tray (publish self-contained para el installer)
dotnet publish FotoshowTray/FotoshowTray.csproj -c Release -r win-x64 --self-contained false

# Compilar shell extension
dotnet build FotoshowShell/FotoshowShell.csproj -c Release

# Compilar ai_worker (requiere Python + PyInstaller/Nuitka instalados)
pyinstaller ai_worker.spec
# o con Nuitka:
nuitka --onefile ai_worker.py

# Generar installer (requiere Inno Setup en PATH)
iscc setup/setup.iss
```

### Debug sin installer
```powershell
# Correr el tray directo (sin instalar)
dotnet run --project FotoshowTray --

# Simular clic derecho en foto
dotnet run --project FotoshowTray -- --add-file "C:\fotos\DSC_0001.jpg"

# Simular clic derecho en carpeta
dotnet run --project FotoshowTray -- --add-folder "C:\fotos\evento_2026"
```

### Registrar overlay icons en desarrollo
```powershell
# Requiere admin + .NET Framework 4.8
cd FotoshowShell\bin\Debug\net48
regasm /codebase FotoshowShell.dll

# Para desregistrar
regasm /unregister FotoshowShell.dll

# Reiniciar Explorer para que tome los cambios
taskkill /f /im explorer.exe && start explorer.exe
```

## Archivos que faltan (hay que crear)
```
FotoshowTray/Resources/
  fotoshow.ico          ← Icono principal de la app (16x16, 32x32, 48x48 en un .ico)
  overlay_pending.ico   ← Círculo gris pequeño (10x10)
  overlay_synced.ico    ← Círculo azul pequeño
  overlay_sold.ico      ← Círculo verde pequeño
setup/icons/
  overlay_pending.ico   ← Copia para el installer
  overlay_synced.ico
  overlay_sold.ico
```
Los overlay icons deben ser de **10x10 px** máximo para que Windows los muestre correctamente sobre el ícono del archivo.

## Backend — endpoints que hay que agregar en fotoshow.online

### POST /api/desktop/sync
Recibe thumbnail + embedding de una foto (sin la foto original).
```
multipart/form-data:
  thumbnail: file (JPEG)
  local_path: string
  path_hash: string (SHA-256 del path)
  faces_detected: int
  file_size: int
  embedding: string (JSON array de floats, opcional)
  gallery_id: string (UUID, opcional)

Response 200:
  { "photo_id": "uuid" }
```

### POST /api/desktop/deliver
Recibe la foto original cuando hay una venta. El backend la sube a R2 y entrega al comprador.
```
multipart/form-data:
  photo: file (JPEG/PNG original)
  order_item_id: string (UUID)

Response 200:
  { "delivered": true }
```

### WS /api/desktop/events
WebSocket autenticado. El backend pushea cuando hay una venta de una foto del fotógrafo.
```
Headers: Authorization: Bearer <jwt>

Mensaje del servidor (JSON):
{
  "order_item_id": "uuid",
  "photo_local_path": "path_hash (SHA-256)",
  "backend_photo_id": "uuid",
  "buyer_email": "...",
  "event_title": "Maratón 2026"
}
```

### GET /api/auth/google/desktop
Login Google para la app de escritorio. Redirige de vuelta con URI scheme.
```
Query params: redirect_uri=fotoshow://auth
Después del login redirige a: fotoshow://auth?token=<jwt>
La app captura esto via el URI scheme registrado en el installer.
```

## SQLite compartido entre procesos
- Path: `%APPDATA%\Fotoshow\photos.db`
- WAL mode activado → FotoshowTray escribe, FotoshowShell lee sin bloquear
- Tabla `Photos`: `Id, LocalPath, PathHash, ThumbnailPath, Embedding, Status, BackendPhotoId, ...`
- `PathHash` = SHA-256 del path normalizado en lowercase (función `LocalDb.HashPath()`)
- La shell extension lee `Status` por `PathHash` para mostrar el overlay correcto

## Named Pipe
- Nombre: `FotoshowTray`
- Formato: una línea JSON por conexión
- Acciones: `add_file`, `add_folder`, `ping`, `auth_token`
- Si el tray no está corriendo cuando se manda un comando, se inicia automáticamente

## URI Scheme: fotoshow://
- Registrado por el installer en `HKLM\SOFTWARE\Classes\fotoshow`
- Usado para el callback del login Google
- Ejemplo: `fotoshow://auth?token=eyJ...`
- `Program.cs` lo captura con arg `--auth-token`

## Context menu (sin COM — via registry)
Registrado en `HKLM\SOFTWARE\Classes\`:
- `Directory\shell\FotoshowAddFolder` → carpetas
- `Directory\Background\shell\FotoshowAddFolder` → fondo de carpeta
- `SystemFileAssociations\image\shell\FotoshowAddPhoto` → cualquier imagen

Ejecutan `FotoshowTray.exe --add-folder "%1"` o `--add-file "%1"`.

## Overlay icons (COM — FotoshowShell.dll)
Registrado en `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\`:
- `"  FotoshowPending"` → GUID A1B2C3D4-0001...
- `"  FotoshowSynced"` → GUID A1B2C3D4-0002...
- `"  FotoshowSold"` → GUID A1B2C3D4-0003...

Los dos espacios al inicio son intencionales — Windows ordena alphabéticamente y los primeros 15 handlers ganan. OneDrive y Dropbox usan el mismo truco.

## Convenciones
- Idioma UI y comentarios: **español**
- Sin ventanas, sin formularios — todo vía tray icon y web
- Thumbnails: solo para sync al backend, nunca mostrar en pantalla localmente
- La foto original nunca sale de la PC hasta que hay una venta confirmada
- No auto-commit sin pedirlo explícitamente
- Target platforms: Windows 10 64-bit o superior

## División de trabajo
- **Este repo (fotoshowescritorio)**: solo app de escritorio Windows — se trabaja en local (Windows)
- **fotoshow-v2**: solo backend FastAPI — se trabaja en el servidor Vultr
- No mezclar: si estás en local, no toques el repo del servidor y viceversa

## Estado actual (2026-03-02)
- [x] FotoshowTray: estructura completa, falta compilar y testear
- [x] FotoshowShell: overlay icons con SharpShell, falta compilar y testear
- [x] setup.iss: installer completo, falta compilar con iscc
- [ ] Iconos (.ico) — pendiente crear
- [ ] Endpoints del backend — pendiente implementar en fotoshow.online
- [ ] Test end-to-end completo
