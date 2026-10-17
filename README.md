# SystemMind
# SystemMind

SystemMind is a native cross-platform desktop system monitor built with C#/.NET and Avalonia UI. It pairs a live dashboard with a chat-based system advisor.

## Included now

- Live RAM, CPU-load, and disk capacity overview
- Largest memory-consuming processes
- Disk overview and safe cleanup guidance
- Contextual health recommendations
- A chat panel with local, privacy-friendly fallback advice
- Optional OpenAI-backed chat: the app sends a concise live resource snapshot alongside the question

## Run it

```bash
dotnet run
```

To enable the OpenAI assistant, add the key to the local `.env` file, then export it into your shell before launching:

```bash
set -a
source .env
set +a
dotnet run
```

Without a key, the assistant still answers RAM, storage, cleanup, and general compatibility questions using local rules. With a key, it uses `gpt-4.1-mini` through the Responses API for more natural, tailored explanations.

## Architecture

- `Services/SystemMonitor.cs` reads OS resource data and processes into a `SystemSnapshot`.
- `Services/AdvisorService.cs` provides local recommendations or securely calls the AI endpoint.
- `ViewModels/MainViewModel.cs` turns snapshots into UI-ready dashboard items and chat messages.
- `MainWindow.axaml` is the native Avalonia desktop interface.

## Next useful additions

For a fuller WinDirStat-style explorer, add an explicit, cancellable directory scanner with per-folder sizes and a treemap; never scan or delete files silently. Game compatibility can be made precise by accepting a game name/requirements and adding platform-specific CPU/GPU probes, then comparing each requirement against the snapshot.
