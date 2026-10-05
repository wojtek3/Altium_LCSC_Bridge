# Third-party notices

## EasyEDALoader

The compiled Altium adapter contains modified source from
[EasyEDALoader](https://github.com/expired6978/EasyEDALoader), commit
`1ade34da9c59ce53d27199452c4f7df9184d4789`.

Copyright © 2025 Expired.

EasyEDALoader is licensed under the GNU General Public License version 3. The
complete corresponding adapter source and its `LICENSE` file are included in
the distribution under `source/LcscBridge.AltiumAdapter`.

The adaptation adds the versioned file bridge, capability handshake, strict
path checks, metadata transfer, and transactional native-library creation.
The GPL license covers the software. It does not grant rights to component
models downloaded from a provider.

## Other dependencies

The companion uses Microsoft WebView2 and Microsoft.Data.Sqlite under their
respective package licenses. The adapter uses Newtonsoft.Json under the MIT
license. See the NuGet package metadata and license files in the source tree.
