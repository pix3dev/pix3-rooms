# Third-Party Notices

Pix3 Rooms depends on the components below. Each remains under its own licence;
nothing here modifies those terms.

This inventory is maintained by hand — the surface is two runtime packages, so a
generator would cost more than it saves. After changing packages, list what is
declared and check each licence against its `.nuspec`:

```bash
dotnet list package                       # what is declared
dotnet nuget locals global-packages -l    # where the nuspecs live
# then: grep '<license' <pkg>/<ver>/<pkg>.nuspec
```

## Runtime dependencies

| Component | Version | Licence | Used by |
|---|---|---|---|
| [MemoryPack](https://github.com/Cysharp/MemoryPack) | 1.21.4 | MIT | Pix3.Rooms.Protocol |
| [Microsoft.IdentityModel.JsonWebTokens](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet) | 8.21.0 | MIT | Pix3.Rooms.Server |

## Test-only dependencies

Not shipped in any deployed artifact.

| Component | Version | Licence |
|---|---|---|
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 3.1.4 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 6.0.4 | MIT |

## Notes

The dependency surface here is deliberately small — two runtime packages, both
MIT — and there is no vendored third-party source in `src/` or `tools/`. Keep it
that way: a gateway that terminates untrusted sockets is the wrong place to grow
a dependency tree, and every addition is a licence decision as well as a
security one.

`Microsoft.IdentityModel.JsonWebTokens` is MIT despite the Microsoft branding;
this was verified from the package's own nuspec rather than assumed.

### The .NET runtime itself

Deployments are self-contained, so a published artifact embeds the .NET runtime,
which is MIT-licensed by Microsoft (with the .NET Library
[licence terms](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm)
applying to some components). Redistributing a self-contained build is expressly
permitted; no additional notice is required beyond preserving Microsoft's.
