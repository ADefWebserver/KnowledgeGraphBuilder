var builder = DistributedApplication.CreateBuilder(args);

// Dev-time only: launches both apps. Neither app reads anything from Aspire.
builder.AddMauiProject("knowledgegraphbuilder", "../KnowledgeGraphBuilder/KnowledgeGraphBuilder.csproj")
       .AddWindowsDevice();                                    // resource "knowledgegraphbuilder-windows"
builder.AddProject<Projects.KnowledgeGraphBuilder_Web>("web"); // the standalone WASM dev server

builder.Build().Run();
