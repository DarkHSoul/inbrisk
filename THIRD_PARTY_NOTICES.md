# Third-Party Notices

Inbrisk is built on .NET 8 (MIT, © Microsoft Corporation) and uses the
following third-party packages. Licenses were taken from each package's
NuGet metadata (`<license>` / bundled LICENSE file) as restored in the local
package cache — regenerate this file whenever PackageReferences change.

The shipped binary (`inbrisk.exe` / `InbriskSetup.exe`) is a self-contained
single-file publish: it bundles the .NET 8 runtime, the Windows Desktop
(WPF) runtime, and every package listed below into the shipped executable.
All listed components therefore ship inside the product.

## Runtime / shipping dependencies (src/)

| Package | Version | License | Copyright / Project |
|---|---|---|---|
| ModelContextProtocol | 2.1.0 | Apache-2.0 | © Model Context Protocol, a Series of LF Projects, LLC — https://csharp.sdk.modelcontextprotocol.io/ |
| Microsoft.Extensions.Hosting | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.Console | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Interop.UIAutomationClient | 10.19041.0 | MIT (bundled LICENSE.txt, © 2019 Roman) | Roemer — https://github.com/Roemer/UIAutomation-Interop |
| Microsoft.Windows.CsWin32 | 0.3.335 | MIT | © Microsoft Corporation — https://github.com/Microsoft/CsWin32 |
| System.Drawing.Common | 8.0.0 | MIT | © Microsoft Corporation — https://github.com/dotnet/winforms |

Declared in: `src/Inbrisk.Mcp/Inbrisk.Mcp.csproj`,
`src/Inbrisk.Platform.Windows/Inbrisk.Platform.Windows.csproj`,
`tools/RemoteOpsProbe/RemoteOpsProbe.csproj` (Interop.UIAutomationClient).

## Bundled transitive dependencies (resolved graph of the shipped binary)

These packages are not declared directly but are pulled in transitively and
bundled into the shipped executable (resolved via
`src/Inbrisk.Cli/obj/project.assets.json`).

| Package | Version | License | Copyright / Project |
|---|---|---|---|
| ModelContextProtocol.Core | 2.1.0 | Apache-2.0 | © Model Context Protocol, a Series of LF Projects, LLC — https://csharp.sdk.modelcontextprotocol.io/ |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Caching.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.Binder | 8.0.2 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.CommandLine | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.FileExtensions | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.Json | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Configuration.UserSecrets | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Diagnostics | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.FileProviders.Physical | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.FileSystemGlobbing | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.Configuration | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.Debug | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.EventLog | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Logging.EventSource | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Options | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Options.ConfigurationExtensions | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Extensions.Primitives | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| Microsoft.Win32.SystemEvents | 8.0.0 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.Diagnostics.DiagnosticSource | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.Diagnostics.EventLog | 8.0.1 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.IO.Pipelines | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.Net.ServerSentEvents | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.Text.Encodings.Web | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |
| System.Text.Json | 10.0.10 | MIT | © Microsoft Corporation — https://dot.net/ |

## Test-only dependencies (tests/) — not shipped

| Package | Version | License | Copyright / Project |
|---|---|---|---|
| coverlet.collector | 6.0.0 | MIT | tonerdo — https://github.com/coverlet-coverage/coverlet |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT (bundled LICENSE_MIT.txt) | © Microsoft Corporation — https://github.com/microsoft/vstest |
| xunit | 2.5.3 | Apache-2.0 | © .NET Foundation — https://xunit.net/ |
| xunit.runner.visualstudio | 2.5.3 | Apache-2.0 | © .NET Foundation — https://xunit.net/ |

Declared in: `tests/Inbrisk.Tests/Inbrisk.Tests.csproj`.

## Build tooling (not redistributed in the product)

- **Inno Setup 7** — compiles `installer/inbrisk.iss` into InbriskSetup.exe.
  Source-available, free for commercial use —
  https://jrsoftware.org/isinfo.php

## Notes

- The bundled transitive list above is the resolved graph for the shipping
  binary (`src/Inbrisk.Cli/obj/project.assets.json`, target
  `net8.0-windows10.0.19041`). Regenerate after any PackageReference change.
- The self-contained publish also bundles the .NET 8 runtime and the
  Windows Desktop (WPF) runtime — MIT, © Microsoft Corporation.
- `Microsoft.Windows.CsWin32` is a source generator (`PrivateAssets="all"`):
  no CsWin32 assembly ships, but its generated interop code is compiled into
  the product; it is listed above for completeness.
- No package declares a copyleft (GPL/LGPL) license; all are MIT or
  Apache-2.0 per package metadata.
