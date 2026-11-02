# apps/unity/Packages — locally vendored packages

This folder holds UPM packages the project consumes from disk instead of the Unity
registry. Everything here is resolved offline by Unity's package manager; no Hub
sign-in is involved.

## com.unity.nuget.newtonsoft-json

| | |
| --- | --- |
| Package id | `com.unity.nuget.newtonsoft-json` |
| Version | `3.2.1` (Newtonsoft.Json 13.0.2) |
| Tarball | `com.unity.nuget.newtonsoft-json-3.2.1.tgz` (1614376 bytes) |
| Source URL | <https://download.packages.unity.com/com.unity.nuget.newtonsoft-json/-/com.unity.nuget.newtonsoft-json-3.2.1.tgz> |
| SHA256 | `677bfa857a567d237b905b350f8058c8a3838e22077131e712f0d004700f74c2` |

`manifest.json` depends on it as:

```json
"com.unity.nuget.newtonsoft-json": "file:com.unity.nuget.newtonsoft-json-3.2.1.tgz"
```

Why a tarball instead of the registry version string: the UPM registry gate on this
machine refuses the download with `You are not signed in. Please sign in using the
Hub and retry`, so a version requirement cannot be satisfied offline. A `file:`
tarball is resolved from disk, needs no credentials, and keeps the official package
(its own LICENSE.md, link.xml, AOT variants and .meta files) as the single copy of
Newtonsoft in this repository. Do not add a second copy under `Assets/Plugins`.

Refresh the hash after re-downloading:

```powershell
Get-FileHash -Algorithm SHA256 apps/unity/Packages/com.unity.nuget.newtonsoft-json-3.2.1.tgz
```
