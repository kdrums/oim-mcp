# McpServerOneIdentityApi

A homegrown [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server for
[One Identity Manager](https://www.oneidentity.com/products/identity-manager/), designed
for use with Claude on Windows.

Built on the same conventions as [claude-graph-mcp](https://github.com/kdrums/claude-graph-mcp):
single self-contained `.exe`, built-in install/uninstall CLI, auto-configures both
Claude Desktop and Claude Code.

---

## Prerequisites

- .NET 9 SDK
- One Identity Manager 8.x or 9.x (Application Server REST API enabled)
- An OAuth2 client registered in RSTS with `client_credentials` grant
- The service account must have the `AppServer_API` program function assigned

---

## Build

```powershell
.\build.ps1
# Output: artifacts\dist\<version>\McpServerOneIdentityApi.exe
```

Optional Authenticode signing for Software Center distribution:

```powershell
.\build.ps1 `
  -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe" `
  -CodeSigningCertificateThumbprint "<thumbprint>" `
  -RequireSignature
```

---

## Install

```powershell
McpServerOneIdentityApi.exe --install `
  --oim-base-url    https://oimserver/AppServer `
  --token-endpoint  https://oimserver/rsts/oauth2/token `
  --client-id       <client-id> `
  --client-secret   <client-secret> `
  --scope           openid `
  --client          auto
```

`--client auto` (default) detects Claude Desktop and Claude Code automatically.
Use `--client desktop`, `--client claude-code`, or `--client both` to target explicitly.

Machine-wide install (requires elevation):

```powershell
McpServerOneIdentityApi.exe --install --machine `
  --oim-base-url   https://oimserver/AppServer `
  --token-endpoint https://oimserver/rsts/oauth2/token `
  --client-id      <client-id> `
  --client-secret  <client-secret>
```

---

## Test authentication

```powershell
McpServerOneIdentityApi.exe --test-auth `
  --token-endpoint https://oimserver/rsts/oauth2/token `
  --client-id      <client-id> `
  --client-secret  <client-secret> `
  --scope          openid
```

---

## Uninstall

```powershell
McpServerOneIdentityApi.exe --uninstall
```

---

## Environment variables

| Variable            | Required | Description |
|---------------------|----------|-------------|
| `OIM_BASE_URL`       | Yes      | Base URL of the OIM Application Server or API Server, e.g. `https://oimserver/AppServer` |
| `OIM_TOKEN_ENDPOINT` | Yes      | RSTS OAuth2 token endpoint, e.g. `https://oimserver/rsts/oauth2/token` |
| `OIM_CLIENT_ID`      | Yes      | OAuth2 client ID registered in RSTS |
| `OIM_CLIENT_SECRET`  | Yes      | OAuth2 client secret |
| `OIM_SCOPE`          | No       | Space-separated scopes (default: `openid`) |

---

## MCP Tools

All tools are **read-only**. `oim-api` is locked to HTTP GET.

| Tool | Description |
|------|-------------|
| `oim-whoami` | Verify connectivity — queries the OIM database info |
| `oim-api` | Read-only GET against any AppServer/ApiServer path. Returns raw JSON. |
| `oim-search-person` | Search persons by name, account, or email |
| `oim-get-person-accounts` | List all target-system accounts for a person |
| `oim-get-pending-approvals` | List open IT Shop approval tasks |
| `oim-list-entities` | List OIM tables (`DialogTable`). TSV: Name, DisplayName, IsCustom. |
| `oim-describe-entity` | List columns of a table (`DialogColumn`). TSV: Column, Type, IsKey, IsFK, FKTableUID. |
| `oim-list-itshop-structure` | List IT Shop hierarchy (`ITShopOrg`). TSV: UID, Name, Type, ParentUID. |
| `oim-list-roles` | List application roles (`AERole`). TSV: UID, Name, Description, ParentUID. |
| `oim-list-approval-workflows` | List approval workflow definitions (`QERWorkingMethod`). TSV: UID, Name, Description. |

The `oim-list-*` tools return TSV with a trailing `# N of M (next startIndex=X)` line for pagination, which uses ~50% fewer tokens than raw JSON.

### Example prompts

> "Find all accounts for John Smith in OIM"

> "Show me the pending approval requests in Identity Manager"

> "What AD groups does jsmith belong to?"

> "List persons in the Finance department who are marked for deletion"

---

## Files

```
├── src/McpServerOneIdentityApi/
│   ├── McpServerOneIdentityApi.csproj
│   └── Program.cs
├── eng/
│   └── build.ps1          # Full build + sign + manifest
├── build.ps1              # Root wrapper → eng/build.ps1
├── McpOneIdentity.sln
└── README.md
```

---

## Install paths

| Scope    | Path |
|----------|------|
| Per-user | `%LOCALAPPDATA%\Programs\McpServerOneIdentityApi\` |
| Machine  | `%ProgramFiles%\McpServerOneIdentityApi\` |

Logs: `%LOCALAPPDATA%\McpServerOneIdentityApi\logs\mcp-server-oimApi.log`

---

## License

MIT
