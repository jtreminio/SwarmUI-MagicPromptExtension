Run the C# prompt parsing regression checks against an existing SwarmUI build:

```sh
dotnet run --project Tests/PromptTags.csproj
```

The default binary directory is SwarmUI's `src/bin/live_release`. Override it with `-p:SwarmBinaryDirectory=/absolute/path/to/swarm/bin` when needed. The checks compile the extension sources and use SwarmUI's actual prompt parser without calling an LLM or image backend.

Run the frontend regression checks:

```sh
node --test Tests/console-errors.test.cjs
```
