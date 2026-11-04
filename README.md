# Knowledge Graph Builder

A workbench for a knowledge graph made of Markdown pages: a folder and page explorer, a Markdown editor with a Links panel (outgoing links, backlinks, unresolved links), and a chat that answers from the graph with `[[citations]]` and proposes page changes that you approve from a diff.

**Try it live in your browser: [BlazorKnowledgeGraphBuilder.azurewebsites.net](https://blazorknowledgegraphbuilder.azurewebsites.net)** — no install needed. It's the WebAssembly app running entirely in your browser: your pages and settings are kept in your browser's local storage, and the chat uses your own API key, sent straight from your browser to the LLM provider.

The design is in [Docs/KnowledgeGraphBuilder-Plan.md](Docs/KnowledgeGraphBuilder-Plan.md) and the UI mockups in [Docs/design](Docs/design/README.md).

## Projects

| Project | What it is |
|---|---|
| `KnowledgeGraphBuilder.Shared` | Razor Class Library with all the UI and logic: storage, graph, Markdown, chat engine, LLM clients |
| `KnowledgeGraphBuilder` | .NET MAUI Blazor Hybrid app. Windows is the supported target. |
| `KnowledgeGraphBuilder.Web` | Standalone Blazor WebAssembly app, published as static files. No server. |
| `KnowledgeGraphBuilder.Tests` | xUnit tests for Shared |
| `KnowledgeGraphBuilder.AppHost` | Aspire AppHost for development only; it launches both apps |

The two apps are independent. They don't share pages, settings or keys, and neither needs Aspire at runtime.

## Running

Windows desktop app:

```bash
dotnet build KnowledgeGraphBuilder/KnowledgeGraphBuilder.csproj -t:Run -f net10.0-windows10.0.19041.0
```

Browser app (dev server at http://localhost:5022):

```bash
dotnet run --project KnowledgeGraphBuilder.Web
```

Both apps through Aspire:

```bash
aspire run
```

Tests:

```bash
dotnet test KnowledgeGraphBuilder.Tests/KnowledgeGraphBuilder.Tests.csproj
```

## Where data and keys live

| | Windows app | Browser app |
|---|---|---|
| Pages | `.md` files under `Documents\KnowledgeGraphBuilder` | Browser `localStorage`: one `kgb:v1:page:<path>` key per page plus `kgb:v1:index` |
| Settings | Preferences (`kgb.llm.*`) | `localStorage` key `kgb:v1:settings` |
| API key | SecureStorage (DPAPI, per Windows user) | `kgb:v1:settings`, **unencrypted**. Any script or extension with access to the page can read it. Use a key with a spending limit and use **Clear key** on shared computers. |
| Deletes | Windows Recycle Bin | Permanent (the confirmation offers **Export first**) |

On first run each app seeds four pages: `Welcome.md` and three pages under `Getting Started`.

Edits made in other editors (Windows) or in another tab (browser) are detected when you save, and you choose Reload, Overwrite or Cancel.

## Browser storage, quota and backup

`localStorage` holds roughly 5 MB per site. **Settings → Backup and storage** shows an estimate and warns at 80%. When a save would go over the quota, the save fails, the change is rolled back and nothing is lost.

Clearing site data removes every page, so export backups: **Export (.zip)** downloads `knowledge-graph-<date>.zip` with pages and folders only (never settings or the API key). **Import (.zip)** accepts up to 10 MB; *Merge* adds pages that don't exist yet, *Replace* deletes every page first. Entries that aren't valid `.md` paths are rejected.

## AI providers

Open **Settings**, choose a provider and model, enter the key, and click **Test connection**. Calls go straight from the app (or the browser) to the provider; there is no proxy.

| Provider | Notes |
|---|---|
| Anthropic (default, `claude-sonnet-5-5`) | Built-in client. In the browser it sends the `anthropic-dangerous-direct-browser-access` CORS header. |
| OpenAI | Uses the OpenAI SDK. |
| Azure OpenAI | Needs the endpoint (`https://<resource>.openai.azure.com/`) and the deployment name as the model. The resource's CORS settings must allow the browser app's origin. |
| Ollama | Needs the endpoint, for example `http://localhost:11434/`. For the browser app, the Ollama host must allow its origin: set `OLLAMA_ORIGINS` (for example `OLLAMA_ORIGINS=http://localhost:5022`) and restart Ollama. |
| Fake | Debug builds only. A scripted client for demos without a key. |

Page text the assistant reads is sent to the selected provider. Every write the assistant proposes is shown as a diff and needs **Apply**; the assistant can't delete, rename or move pages.

## Publishing the browser app

```bash
dotnet publish KnowledgeGraphBuilder.Web -c Release
```

The static site is in `KnowledgeGraphBuilder.Web/bin/Release/net10.0/publish/wwwroot`. Any static host works if it falls back to `index.html` for unknown paths (for example `npx serve -s`). To host under a sub-path, change `<base href="/">` in `index.html` to that path, such as `/kgb/`.

## Editing notes

- Front-matter is edited in the **Properties** box and never passes through the Markdown editor.
- **Design** mode is the Radzen Markdown editor; **Source** edits the Markdown text directly; **Preview** renders the page with clickable links. Unresolved links open a New page dialog.
- Keyboard: Ctrl+S saves, F2 renames and Delete deletes in the tree, Enter sends a chat message (Shift+Enter adds a line), Esc stops a streaming reply.
