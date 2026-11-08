# MDX parser third-party notices

This directory contains components with different licenses. `LICENSE.txt`
contains the Apache License 2.0 for the parser portions originally imported
with that license; it does not relicense the other components listed below.

## MiniLZO

`encrypt/MiniLZO.cs` incorporates Frank Razenberg's C# port of miniLZO 2.06:
https://github.com/zzattack/MiniLZO/blob/master/MiniLZO/MiniLZO.cs

The original LZO implementation is Copyright (C) 1996–2011 Markus Franz Xaver
Johannes Oberhumer, licensed under GPL version 2 or, at your option, any later
version (`GPL-2.0-or-later`). Kkindle distributes this incorporated implementation
using the GPL v3 option; the complete selected license is in
`LICENSE.MiniLZO.txt`, and Kkindle's corresponding source is provided under the
root GPL license. The Apache license in this directory does not apply to it.

This copy uses the `MdxParser.encrypt` namespace and contains C# expression and
formatting adaptations. The original attribution was restored on 2026-11-08;
that update does not change the compression/decompression implementation.

## Digest implementations

`encrypt/RipeMD128.cs`, `GeneralDigest.cs`, `Pack.cs`, `IDigest.cs` and
`IMemoable.cs` incorporate Bouncy Castle digest/API implementations with
namespace and C# adaptations. The Bouncy Castle copyright and permissive
license are retained in `LICENSE.BouncyCastle.html`. The original import did
not record its precise revision; the historical upstream source is available at:

- https://github.com/bcgit/bc-csharp/blob/release-1.9.0/crypto/src/crypto/digests/GeneralDigest.cs
- https://github.com/bcgit/bc-csharp/blob/release-1.9.0/crypto/src/crypto/digests/RipeMD128Digest.cs
- https://github.com/bcgit/bc-csharp/blob/release-1.9.0/crypto/License.html

## Salsa20

`encrypt/Salsa20.cs` retains the reference to Bradley Grainger's
“Salsa20 Implementation in C#” (2008-06-10). The author explicitly places this
implementation, like the reference C implementation, in the public domain:
https://faithlife.codes/blog/2008/06/salsa20_implementation_in_c_1/

These notices and the accompanying original licenses must travel with the
incorporated components in Kkindle's build and publish outputs.
