# Native AZW3 reader fixtures

These small test books contain text authored for Kkindle tests, a one-pixel PNG,
a two-level chapter directory, CSS, Unicode text and links between chapters.
They were generated from the same EPUB using Calibre's AZW3 writer, with default
PalmDOC compression, `--dont-compress`, and `--mobi-file-type both`, respectively.
The hybrid MOBI is named `.azw3` to exercise KF8 discovery in hybrid containers.
The reader and tests do not invoke Calibre or require it to be installed.

`native-legacy.mobi` and `native-legacy-uncompressed.mobi` contain the same
authored text/images/links, written with `--mobi-file-type old` (and
`--dont-compress` for the latter). They exercise native MOBI6 decoding,
byte-offset links and NCX navigation (Calibre flattens the legacy directory).
The hybrid fixture retains its hierarchical directory. Calibre was used only to
generate these checked-in fixtures, never by the tests or reader.

`native-compressed-assembled.dat` is an independent byte-position oracle for
the authored compressed fixture. It is KindleUnpack's unmodified debug
`mobi8/assembled_text.dat` output (upstream commit
`bf0ca6ece4e73494625e7950be3e259b6260774c`). The UTF-8 selection
`你好，世界。😀` occupies bytes 1537–1559. It deliberately tests reconstructed
KF8 reading order instead of round-tripping positions through our own parser.
