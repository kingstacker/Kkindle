# KindleUnpack attribution

The managed KF8 index, PalmDOC and HUFF/CDIC decompression, and skeleton/fragment
reconstruction in `Azw3Binary.cs` and `Azw3ReaderService.cs` are adapted from the
KindleUnpack project's binary format algorithms:
https://github.com/kevinhendricks/KindleUnpack

KindleUnpack contributors include Paul Durrant, Kevin Hendricks, DiapDealer,
and the other contributors identified in the upstream repository. KindleUnpack
is distributed under GPL v3 or later. These adaptations are distributed under
the same license; the complete GPL v3 text is included in `LICENSE.txt` here.

The application uses managed C# code; Python and Calibre are not needed to read
unencrypted AZW3. No DRM decryption is implemented.
