# Third-party software

Release archives contain a self-contained exec-mcp binary, which bundles the .NET runtime and the libraries below.
They also contain sni-router, built with Go, and zod in `sdk/node/node_modules`. The licence texts are in
[licenses/](licenses), and archives ship this folder.

| Component | Version | License | Text |
| --- | --- | --- | --- |
| .NET runtime | 10.0 | MIT | [dotnet-runtime.txt](licenses/dotnet-runtime.txt) |
| ASP.NET Core | 10.0 | MIT | [aspnetcore.txt](licenses/aspnetcore.txt) |
| ModelContextProtocol C# SDK (`ModelContextProtocol`, `.Core`, `.AspNetCore`) | 2.3.0 | Apache-2.0 (parts MIT) | [modelcontextprotocol-csharp-sdk.txt](licenses/modelcontextprotocol-csharp-sdk.txt) |
| Anthropic C# SDK | 13.3.0 | Apache-2.0 | [anthropic-csharp-sdk.txt](licenses/anthropic-csharp-sdk.txt) |
| Jint | 4.17.0 | BSD-2-Clause | [jint.txt](licenses/jint.txt) |
| Acornima | 1.7.0 | BSD-3-Clause | [acornima.txt](licenses/acornima.txt) |
| JsonSchema.Net, JsonPointer.Net, Json.More.Net | 8.0.5, 6.0.1, 2.2.0 | MIT | [json-everything.txt](licenses/json-everything.txt) |
| Ignore | 0.2.1 | MIT | [ignore.txt](licenses/ignore.txt) |
| Humanizer.Core | 3.0.1 | MIT | [humanizer.txt](licenses/humanizer.txt) |
| zod (Node plugin SDK) | 4.x | MIT | [zod.txt](licenses/zod.txt) |
| Go standard library (sni-router) | 1.22+ | BSD-3-Clause | [go.txt](licenses/go.txt) |

JsonSchema.Net is pinned to 8.0.5 on purpose. From 9.0 on, the json-everything NuGet binaries ship under a
maintenance-fee EULA (OSMF), while 8.x is plain MIT. Check the license before upgrading.
