using Blazored.LocalStorage;
using KnowledgeGraphBuilder.Shared;
using KnowledgeGraphBuilder.Shared.Hosting;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using KnowledgeGraphBuilder.Shared.Settings;
using KnowledgeGraphBuilder.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The working tree lives in the .NET in-memory file system and is written through to localStorage (D13, D16).
var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var root = Path.Combine(string.IsNullOrEmpty(documents) ? "/home/web_user/Documents" : documents, "KnowledgeGraphBuilder");
var options = new KnowledgeStoreOptions(root);

builder.Services.AddRadzenComponents();
builder.Services.AddBlazoredLocalStorageAsSingleton();
builder.Services.AddSingleton(_ => new HttpClient());
builder.Services.AddSingleton<IHostCapabilities>(new BrowserHostCapabilities(root));
builder.Services.AddSingleton<IKeyValueStore, LocalStorageKeyValueStore>();
builder.Services.AddSingleton<ISettingsStore, BrowserSettingsStore>();
builder.Services.AddSingleton<PersistingKnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeStore>(sp => sp.GetRequiredService<PersistingKnowledgeStore>());
builder.Services.AddKnowledgeGraphShared(options);
builder.Services.AddSingleton<IBackupService, ZipBackupService>();
builder.Services.AddSingleton<StorageEventBridge>();

var host = builder.Build();

var store = host.Services.GetRequiredService<PersistingKnowledgeStore>();
await store.HydrateAsync();
await KnowledgeSeeder.SeedIfEmptyAsync(store);
await host.Services.GetRequiredService<StorageEventBridge>().StartAsync();

await host.RunAsync();
