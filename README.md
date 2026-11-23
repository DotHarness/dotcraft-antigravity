<div align="center">

![DotCraft Antigravity](https://github.com/DotHarness/resources/raw/master/dotcraft-antigravity/banner.png)

[DotCraft](https://github.com/DotHarness/dotcraft) · [License](./LICENSE) · [Spec](./specs/antigravity-backend.md)

Use your local Google Antigravity CLI (`agy`) account in DotCraft.

</div>

## Prerequisites

Install the official [Antigravity CLI (`agy`)](https://antigravity.google/download) and sign in once with `agy` or `agy acp --login`.

## Install

1. Install **Antigravity** from DotCraft's plugin marketplace and enable it.
2. In provider settings, add an **Antigravity** provider, verify or sign in with your local `agy` CLI account, and select a model.

## Build

Requires the .NET 10 SDK, Node.js 20 or later, and PowerShell 7.

```powershell
.\build.bat
```

## Test

```powershell
dotnet test dotcraft-antigravity.sln
npm --prefix desktop test
```
