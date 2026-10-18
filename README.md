# DiagnoX — WinDirStat with an AI Agent

DiagnoX is a native cross-platform disk usage analyzer and system manager built with C#/.NET 10 and Avalonia UI. It blends classic **WinDirStat** (interactive cushion treemap, directory hierarchy, extension statistics) with an **AI Agent Copilot** that understands your filesystem, identifies space hogs, and guides you through safe cleanups.

---

## 🚀 Key Features

### 1. 📊 WinDirStat Storage Explorer
- **High-Throughput Asynchronous Scanner**: Multi-threaded, cancellable recursive scanner scanning the full root filesystem (`/`) by default, complete with live progress reporting (items/sec, accumulated size, elapsed time).
- **Directory Hierarchy View**: Hierarchical folder tree sorted descending by size, complete with formatted units (GB, MB, KB), percent-of-parent bars, file counts, and subfolder counts.
- **Interactive Cushion Treemap (`TreemapControl`)**:
  - Squarified treemap visualizer rendering files and folders proportional to their disk footprint.
  - Color-coded by file category (Videos, Audio, Images, Archives, Code, Documents, Binaries, Caches).
  - Hover inspection with file metadata and path.
  - Click to select and highlight nodes in the tree hierarchy.
  - Double-click to reveal in the system file manager.
- **File Extension Breakdown Panel**: Classic WinDirStat extension table grouping files by type with matching treemap color blocks, total size, file count, and space percentage.
- **Safe File Operations**:
  - Reveal in OS File Manager (`xdg-open` / `explorer` / `open`).
  - Open terminal at target location.
  - Copy full path to clipboard.
  - Safe Move-to-Trash (`gio trash` / `.local/share/Trash`).
  - Protected path guardrails: prevents deletion of critical system directories (`/`, `/bin`, `/boot`, `/etc`, `/usr`, `/lib`, and user home root).
  - Single-click **"Ask AI"** button on any file or folder to check what it is and whether it is safe to remove.

### 2. 🤖 AI System & Storage Copilot
- **Scan-Aware Reasoning**: When a scan completes, the AI receives full context: scanned path, total size, top 5 largest directories, top 5 largest files, top file extensions, and detected junk candidates.
- **Dual-Engine AI Architecture**:
  - **Cloud AI (OpenRouter)**: Supports OpenRouter chat-completion models with automatic `.env` loading on startup.
  - **Local Expert Storage Engine**: 100% offline, privacy-friendly heuristic intelligence that answers storage breakdown queries, explains unknown folders (`node_modules`, `~/.cache`, `~/.local`), diagnoses memory pressure, and creates tailored cleanup plans even without internet or API keys.
- **Quick Action Prompts**:
  - *"What is taking up all my space? Break down the scan."*
  - *"What can I safely clean up to free disk space?"*
  - *"What is node_modules, and can I delete it?"*
  - *"Why is my RAM being used?"*

### 3. 🧹 Smart Cleanup Assistant
- One-click detection for:
  - Thumbnail and system caches (`~/.cache/thumbnails`, `~/.cache/pip`, etc.)
  - Package manager caches (NPM `_cacache`, Yarn, Rust Cargo crates, Go build caches)
  - User trash bin (`~/.local/share/Trash`)
  - Temporary files (`/tmp`, `/var/tmp`)
  - Developer artifacts (`node_modules`, `bin/`, `obj/`, `target/`)
  - Downloads older than 30 days
- Itemized preview with risk levels (`Safe`, `Review`, `Warning`), reclaimable bytes calculation, and safe multi-select cleanup execution.

### 4. ⚡ Live Performance & Process Manager
- Real-time CPU load across logical cores, RAM usage, and active drives.
- Searchable process table with PID, process name, and working set memory (MB).
- Safe process termination ("End Task").

---

## 🛠️ Run & Test

### Run the App
```bash
dotnet run
```

### Run the Automated Test Suite
```bash
dotnet test DiagnoX.Tests/DiagnoX.Tests.csproj
```

### Enable Cloud AI (Optional)
To use OpenRouter, add your key to `.env` in the project root:
```env
OPENROUTER_API_KEY=sk-or-v1-...
AI_MODEL=meta-llama/llama-3.3-70b:free
```
Or configure keys directly in the in-app **Settings** tab. If no key is set or if rate limits are reached, DiagnoX seamlessly switches to its built-in **Local Expert Storage Engine**.

---

## 🏛️ Architecture

- `Controls/TreemapControl.cs`: Custom Avalonia control rendering squarified treemaps with hover highlights and click selection.
- `Services/DirectoryScanner.cs`: Resilient async file system scanner with atomic progress metrics and cycle protection.
- `Services/TreemapBuilder.cs`: Squarified treemap layout algorithm (Bruls, Huizing, van Wijk).
- `Services/AdvisorService.cs`: Scan-aware AI engine supporting cloud LLMs and rich local expert fallbacks.
- `Services/CleanupScannerService.cs`: Detects reclaimable caches, temporary files, old downloads, and build artifacts.
- `Services/FileOperationsService.cs`: OS shell integration and safe path guardrails.
- `ViewModels/`: `StorageExplorerViewModel`, `CleanupViewModel`, `PerformanceViewModel`, `SettingsViewModel`, `MainViewModel`.
# DiagnoX
