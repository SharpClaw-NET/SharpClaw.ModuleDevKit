# SharpClaw Module Development Kit

Module Development Kit is the SharpClaw module for creating, inspecting,
building, verifying, and hot-loading other SharpClaw modules while the host is
running. It exposes the `mdk` tool prefix, keeps its runtime shape as a .NET
sidecar module, and uses the public `SharpClaw.Contracts` package as its host
contract boundary.

Install the package into a SharpClaw host the same way as any runtime-loadable
SharpClaw module package. The NuGet package carries its runtime payload under
`sharpclaw/`, including `module.json`, the module assembly, dependency file,
and non-framework dependency assemblies required by the sidecar loader.

```powershell
dotnet add package SharpClaw.Modules.ModuleDev --version 0.1.1-beta.1
```

After the host loads the module, agents can call tools such as scaffold, file
read and write, build, runtime verification, load and reload, process
inspection, SDK reference lookup, and conversation
steering workflows through the `mdk` prefix. The module keeps file writes
scoped to SharpClaw's external module workspace and validates module IDs,
relative paths, file names, and build project extensions before touching disk
or starting `dotnet build`.

The package is AGPL-3.0-only. Module authors should keep their own modules
behind `SharpClaw.Contracts` interfaces and package references instead of
referencing SharpClaw runtime internals or host persistence projects directly.
