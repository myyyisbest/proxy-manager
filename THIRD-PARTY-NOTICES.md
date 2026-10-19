# Third-party components

- **sing-box 1.14.3**, SagerNet contributors, GPL-3.0-or-later. The bundled executable is unchanged from the official windows-amd64 release. License: `licenses/GPL-3.0.txt`. Corresponding upstream source is included as `sources/sing-box-1.14.3-source.zip`, with pinned dependency versions in its Go module files. Upstream release and build instructions: https://github.com/SagerNet/sing-box/releases/tag/v1.14.3 and https://sing-box.sagernet.org/installation/build-from-source/ . See `tools/engine.lock.json` in this project's source for file digests.
- **CommunityToolkit.Mvvm 8.4.2**, .NET Foundation and contributors, MIT. License and notices: `licenses/CommunityToolkit.Mvvm/`.
- **WPF-UI 4.3.0**, Leszek Pomianowski and WPF UI contributors, MIT. License and notices: `licenses/WPF-UI/`.
- **Microsoft .NET 8 runtime and Windows Desktop runtime**, Microsoft and contributors. Licenses and notices: `licenses/NET-Runtime/` and `licenses/NET-WindowsDesktop/`.

This package includes the source of ProxyManager for local maintenance. Third-party components retain their respective licenses. The application does not bundle proxy subscription information, credentials, or nodes.
