# Third-Party Notices

Inbrisk is built on .NET 8 (MIT, © Microsoft Corporation) and uses the
following third-party packages. Licenses were taken from each package's
NuGet metadata (`<license>` / bundled LICENSE file) as restored in the local
package cache — regenerate this file whenever PackageReferences change.

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

- Transitive dependencies of the packages above are governed by the .NET /
  NuGet licensing of their respective publishers; the resolved graph is in
  `obj/project.assets.json` per project.
- No package declares a copyleft (GPL/LGPL) license; all are MIT or
  Apache-2.0 per package metadata.
