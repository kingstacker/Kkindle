# Typography incorporated-source notices

CSharpMath.Rendering compiles font and text code from the Typography submodule
at commit `d331968f3ee714332a8ea4fa95a21c15812a8d93`. The complete original project license/attribution
file and TextBreak MIT license accompany this notice, along with the
FreeType FTL and historical ICU license texts referenced by this code.
Apache-2.0 text is included in ../../licenses/Apache-2.0.txt.

The following comment excerpts are copied unchanged from the source files
included by the OpenFont, GlyphLayout and TextBreak shared project imports.
These excerpts preserve individual copyright and licensing notices; they
are an extracted notice document, rather than an upstream LICENSE file.

Sources: [Typography.GlyphLayout/GlyphIndexList.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/GlyphIndexList.cs), [Typography.GlyphLayout/GlyphLayout.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/GlyphLayout.cs), [Typography.GlyphLayout/GlyphPosition.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/GlyphPosition.cs), [Typography.GlyphLayout/GlyphSubstitution.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/GlyphSubstitution.cs), [Typography.GlyphLayout/PixelScaleLayoutExtensions.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/PixelScaleLayoutExtensions.cs)

```text
//MIT, 2016-present, WinterDev
```

Sources: [Typography.GlyphLayout/MeasureStringBox.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/MeasureStringBox.cs), [Typography.GlyphLayout/UserCharToGlyphIndexMap.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.GlyphLayout/UserCharToGlyphIndexMap.cs)

```text
//MIT, 2016-present, WinterDev 
```

Sources: [Typography.OpenFont/AdditionalInfo/AdobeGlyphList.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/AdditionalInfo/AdobeGlyphList.cs)

```text
//BSD, 2015, Adobe Systems Incorporated
//from https://github.com/adobe-type-tools/agl-aglfn/blob/master/glyphlist.txt
//# -----------------------------------------------------------
//# Copyright 2002, 2010, 2015 Adobe Systems Incorporated.
//# All rights reserved.
//#
//# Redistribution and use in source and binary forms, with or
//# without modification, are permitted provided that the
//# following conditions are met:
//#
//# Redistributions of source code must retain the above
//# copyright notice, this list of conditions and the following
//# disclaimer.
//#
//# Redistributions in binary form must reproduce the above
//# copyright notice, this list of conditions and the following
//# disclaimer in the documentation and/or other materials
//# provided with the distribution.
//#
//# Neither the name of Adobe Systems Incorporated nor the names
//# of its contributors may be used to endorse or promote
//# products derived from this software without specific prior
//# written permission.
//#
//# THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
//# CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
//# INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
//# MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//# DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
//# CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
//# SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
//# NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//# LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
//# HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
//# CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
//# OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//# SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//# -----------------------------------------------------------
//# Name:          Adobe Glyph List
//# Table version: 2.0
//# Date:          September 20, 2002
//# URL:           https://github.com/adobe-type-tools/agl-aglfn
//#
//# Format: two semicolon-delimited fields:
//#   (1) glyph name--upper/lowercase letters and digits
//#   (2) Unicode scalar value--four uppercase hexadecimal digits
//#
```

Sources: [Typography.OpenFont/AdditionalInfo/AdobeGlyphListForNewFont.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/AdditionalInfo/AdobeGlyphListForNewFont.cs)

```text
//BSD, 2015, Adobe Systems Incorporated.
//from https://raw.githubusercontent.com/adobe-type-tools/agl-aglfn/master/aglfn.txt
//# -----------------------------------------------------------
//# Copyright 2002, 2003, 2005, 2006, 2008, 2010, 2015 Adobe Systems
//# Incorporated. All rights reserved.
//#
//# Redistribution and use in source and binary forms, with or
//# without modification, are permitted provided that the
//# following conditions are met:
//#
//# Redistributions of source code must retain the above
//# copyright notice, this list of conditions and the following
//# disclaimer.
//#
//# Redistributions in binary form must reproduce the above
//# copyright notice, this list of conditions and the following
//# disclaimer in the documentation and/or other materials
//# provided with the distribution.
//#
//# Neither the name of Adobe Systems Incorporated nor the names
//# of its contributors may be used to endorse or promote
//# products derived from this software without specific prior
//# written permission.
//#
//# THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
//# CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
//# INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
//# MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//# DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
//# CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
//# SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
//# NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//# LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
//# HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
//# CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
//# OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//# SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//# -----------------------------------------------------------
//# Name:          Adobe Glyph List For New Fonts
//# Table version: 1.7
//# Date:          November 6, 2008
//# URL:           https://github.com/adobe-type-tools/agl-aglfn
//#
//# Description:
//#
//# AGLFN (Adobe Glyph List For New Fonts) provides a list of base glyph
//# names that are recommended for new fonts, which are compatible with
//# the AGL (Adobe Glyph List) Specification, and which should be used
//# as described in Section 6 of that document. AGLFN comprises the set
//# of glyph names from AGL that map via the AGL Specification rules to
//# the semantically correct UV (Unicode Value). For example, "Asmall"
//# is omitted because AGL maps this glyph name to the PUA (Private Use
//# Area) value U+F761, rather than to the UV that maps from the glyph
//# name "A." Also omitted is "ffi," because AGL maps this to the
//# Alphabetic Presentation Forms value U+FB03, rather than decomposing
//# it into the following sequence of three UVs: U+0066, U+0066, and
//# U+0069. The name "arrowvertex" has been omitted because this glyph
//# now has a real UV, and AGL is now incorrect in mapping it to the PUA
//# value U+F8E6. If you do not find an appropriate name for your glyph
//# in this list, then please refer to Section 6 of the AGL
//# Specification.
//#
//# Format: three semicolon-delimited fields:
//#   (1) Standard UV or CUS UV--four uppercase hexadecimal digits
//#   (2) Glyph name--upper/lowercase letters and digits
//#   (3) Character names: Unicode character names for standard UVs, and
//#       descriptive names for CUS UVs--uppercase letters, hyphen, and
//#       space
//#
//# The records are sorted by glyph name in increasing ASCII order,
//# entries with the same glyph name are sorted in decreasing priority
//# order, the UVs and Unicode character names are provided for
//# convenience, lines starting with "#" are comments, and blank lines
//# should be ignored.
//#
//# Revision History:
//#
//# 1.7 [6 November 2008]
//# - Reverted to the original 1.4 and earlier mappings for Delta,
//#   Omega, and mu.
//# - Removed mappings for "afii" names. These should now be assigned
//#   "uni" names.
//# - Removed mappings for "commaaccent" names. These should now be
//#   assigned "uni" names.
//#
//# 1.6 [30 January 2006]
//# - Completed work intended in 1.5.
//#
//# 1.5 [23 November 2005]
//# - Removed duplicated block at end of file.
//# - Changed mappings:
//#   2206;Delta;INCREMENT changed to 0394;Delta;GREEK CAPITAL LETTER DELTA
//#   2126;Omega;OHM SIGN changed to 03A9;Omega;GREEK CAPITAL LETTER OMEGA
//#   03BC;mu;MICRO SIGN changed to 03BC;mu;GREEK SMALL LETTER MU
//# - Corrected statement above about why "ffi" is omitted.
//#
//# 1.4 [24 September 2003]
//# - Changed version to 1.4, to avoid confusion with the AGL 1.3.
//# - Fixed spelling errors in the header.
//# - Fully removed "arrowvertex," as it is mapped only to a PUA Unicode
//#   value in some fonts.
//#
//# 1.1 [17 April 2003]
//# - Renamed [Tt]cedilla back to [Tt]commaaccent.
//#
//# 1.0 [31 January 2003]
//# - Original version.
//# - Derived from the AGLv1.2 by:
//#   removing the PUA area codes;
//#   removing duplicate Unicode mappings; and
//#   renaming "tcommaaccent" to "tcedilla" and "Tcommaaccent" to "Tcedilla"
//# 
```

Sources: [Typography.OpenFont/AdditionalInfo/MacPostFormat1.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/AdditionalInfo/MacPostFormat1.cs), [Typography.OpenFont/Tables.Others/VerticalMetrics.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/VerticalMetrics.cs)

```text
//Apache2, 2017-present, WinterDev
```

Sources: [Typography.OpenFont/AdditionalInfo/OS2_IBMFontClassParameters.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/AdditionalInfo/OS2_IBMFontClassParameters.cs)

```text
//Apache2, 2017-present, WinterDev
//https://www.microsoft.com/typography/otspec/ibmfc.htm
```

Sources: [Typography.OpenFont/Bounds.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Bounds.cs), [Typography.OpenFont/Glyph.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Glyph.cs), [Typography.OpenFont/OpenFontReader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/OpenFontReader.cs), [Typography.OpenFont/Tables.Others/Kern.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/Kern.cs), [Typography.OpenFont/Tables/Cmap.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/Cmap.cs), [Typography.OpenFont/Tables/Head.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/Head.cs), [Typography.OpenFont/Tables/HorizontalHeader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/HorizontalHeader.cs), [Typography.OpenFont/Tables/HorizontalMetrics.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/HorizontalMetrics.cs), [Typography.OpenFont/Tables/MaxProfile.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/MaxProfile.cs), [Typography.OpenFont/Tables/NameEntry.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/NameEntry.cs), [Typography.OpenFont/Tables/TableEntry.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/TableEntry.cs), [Typography.OpenFont/Tables/TableEntryCollection.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/TableEntryCollection.cs), [Typography.OpenFont/Tables/TableHeader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/TableHeader.cs), [Typography.OpenFont/Typeface.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Typeface.cs)

```text
//Apache2, 2017-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev
```

Sources: [Typography.OpenFont/Geometry.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Geometry.cs)

```text
//MIT, 2015, Michael Popoloski's SharpFont,
//MIT, 2016-present, WinterDev
```

Sources: [Typography.OpenFont/IGlyphTranslator.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/IGlyphTranslator.cs)

```text
//MIT, 2016-present, WinterDev
//MIT, 2015, Michael Popoloski 
//FTL, 3-clauses BSD, FreeType project
//-----------------------------------------------------
```

Sources: [Typography.OpenFont/IO/ByteOrderSwappingBinaryReader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/IO/ByteOrderSwappingBinaryReader.cs), [Typography.OpenFont/Tables.TrueType/Glyf.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.TrueType/Glyf.cs)

```text
//Apache2, 2014-2016, Samuel Carlsson, WinterDev
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/AttachmentListTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/AttachmentListTable.cs), [Typography.OpenFont/Tables.AdvancedLayout/ClassDefTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/ClassDefTable.cs), [Typography.OpenFont/Tables.AdvancedLayout/CoverageTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/CoverageTable.cs), [Typography.OpenFont/Tables.AdvancedLayout/FeatureInfo.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/FeatureInfo.cs), [Typography.OpenFont/Tables.AdvancedLayout/FeatureList.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/FeatureList.cs), [Typography.OpenFont/Tables.AdvancedLayout/GDEF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/GDEF.cs), [Typography.OpenFont/Tables.AdvancedLayout/GPOS.Others.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/GPOS.Others.cs), [Typography.OpenFont/Tables.AdvancedLayout/GSUB.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/GSUB.cs), [Typography.OpenFont/Tables.AdvancedLayout/IGlyphIndexList.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/IGlyphIndexList.cs), [Typography.OpenFont/Tables.AdvancedLayout/LigatureCaretListTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/LigatureCaretListTable.cs), [Typography.OpenFont/Tables.AdvancedLayout/ScriptList.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/ScriptList.cs), [Typography.OpenFont/Tables.TrueType/Gasp.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.TrueType/Gasp.cs), [Typography.OpenFont/Tables/Post.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/Post.cs)

```text
//Apache2, 2016-present, WinterDev
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/Base.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/Base.cs)

```text
//Apache2, 2016-present, WinterDev
//https://www.microsoft.com/typography/otspec/base.htm
//BASE - Baseline Table
//The Baseline table (BASE) provides information used to align glyphs of different scripts and sizes in a line of text, 
//whether the glyphs are in the same font or in different fonts.
//To improve text layout, the Baseline table also provides minimum (min) and maximum (max) glyph extent values for each script,
//language system, or feature in a font.
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/COLR.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/COLR.cs)

```text
// Copyright © 2017-present Sam Hocevar <sam@hocevar.net>
// Apache2
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/CPAL.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/CPAL.cs)

```text
// Copyright © 2017-present Sam Hocevar <sam@hocevar.net>, WinterDev
// Apache2
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/GPOS.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/GPOS.cs), [Typography.OpenFont/Tables.AdvancedLayout/GlyphShapingTableEntry.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/GlyphShapingTableEntry.cs)

```text
//Apache2, 2016-present, WinterDev, Sam Hocevar <sam@hocevar.net>
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/JustificationTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/JustificationTable.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/BitmapFontGlyphSource.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/BitmapFontGlyphSource.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/BitmapFontsCommon.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/BitmapFontsCommon.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/CBDT.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/CBDT.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/CBLC.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/CBLC.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/EBDT.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/EBDT.cs), [Typography.OpenFont/Tables.BitmapAndSvgFonts/EBSC.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/EBSC.cs), [Typography.OpenFont/Tables.Others/STAT.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/STAT.cs), [Typography.OpenFont/Tables.Variations/AVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/AVar.cs), [Typography.OpenFont/Tables.Variations/CVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/CVar.cs), [Typography.OpenFont/Tables.Variations/Common.ItemVariationStore.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/Common.ItemVariationStore.cs), [Typography.OpenFont/Tables.Variations/Common.TupleVariationStore.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/Common.TupleVariationStore.cs), [Typography.OpenFont/Tables.Variations/FVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/FVar.cs), [Typography.OpenFont/Tables.Variations/GVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/GVar.cs), [Typography.OpenFont/Tables.Variations/HVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/HVar.cs), [Typography.OpenFont/Tables.Variations/MVar.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Variations/MVar.cs)

```text
//MIT, 2019-present, WinterDev
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/MathTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/MathTable.cs)

```text
//MIT, 2018-present, WinterDev
//https://www.microsoft.com/typography/otspec/math.htm
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/ScriptLang.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/ScriptLang.cs), [Typography.OpenFont/Tables.Others/HorizontalDeviceMetrics.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/HorizontalDeviceMetrics.cs), [Typography.OpenFont/Tables.Others/VerticalDeviceMetrics.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/VerticalDeviceMetrics.cs), [Typography.OpenFont/Tables/OS2.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/OS2.cs)

```text
//Apache2, 2016-present, WinterDev 
```

Sources: [Typography.OpenFont/Tables.AdvancedLayout/ScriptTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.AdvancedLayout/ScriptTable.cs)

```text
//Apache2, 2016-present, WinterDev
//https://www.microsoft.com/typography/otspec/chapter2.htm
//https://www.microsoft.com/typography/OTSpec/delta180to181/chapter2.htm
```

Sources: [Typography.OpenFont/Tables.BitmapAndSvgFonts/EBLC.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/EBLC.cs)

```text
//MIT, 2017-present, WinterDev
//MIT, 2015, Michael Popoloski, WinterDev
```

Sources: [Typography.OpenFont/Tables.BitmapAndSvgFonts/SvgTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.BitmapAndSvgFonts/SvgTable.cs)

```text
//Apache2, 2017-present, WinterDev 
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs)

```text
//Apache2, 2018, apache/pdfbox Authors ( https://github.com/apache/pdfbox) 
//
//Apache PDFBox
//Copyright 2014 The Apache Software Foundation
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//This product includes software developed at
//The Apache Software Foundation(http://www.apache.org/).
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Copyright (c) 2002-2007, www.pdfbox.org
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Based on source code originally developed in the PaDaF project.
//Copyright (c) 2010 Atos Worldline SAS
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Includes the Adobe Glyph List
//Copyright 1997, 1998, 2002, 2007, 2010 Adobe Systems Incorporated.
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Includes the Zapf Dingbats Glyph List
//Copyright 2002, 2010 Adobe Systems Incorporated.
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs), [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Includes OSXAdapter
//Copyright (C) 2003-2007 Apple, Inc., All Rights Reserved
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs)

```text
//------------------------------------------------------------------
//many areas are ported from Java code
//Apache2, 2018-present, WinterDev
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs)

```text
        //Table 1 CFF Data Layout
        //Entry                     Comments
        //Header      		        –
        //Name INDEX  		        –
        //Top DICT INDEX 		    –
        //String INDEX		        –
        //Global Subr INDEX	        – 	
        //Encodings			        –		
        //Charsets			        –
        //FDSelect                  CIDFonts only
        //CharStrings INDEX         per-font
        //Font DICT INDEX           per-font, CIDFonts only
        //Private DICT              per-font
        //Local Subr INDEX          per-font or per-Private DICT for CIDFonts
        //Copyright and Trademark	-
        // Notices  	
        //-----------------
```

Sources: [Typography.OpenFont/Tables.CFF/CFF.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFF.cs)

```text
        //from Apache's PDF box/FontBox
        //@author Villu Ruusmann
```

Sources: [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//Apache2, 2018, Apache/PDFBox Authors ( https://github.com/apache/pdfbox) 
//
//
//Apache PDFBox
//Copyright 2014 The Apache Software Foundation
```

Sources: [Typography.OpenFont/Tables.CFF/CFFTable.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CFFTable.cs)

```text
//------------------------------------------------------------------
//Apache2, 2018-present, WinterDev
```

Sources: [Typography.OpenFont/Tables.CFF/CffEvaluationEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/CffEvaluationEngine.cs)

```text
//Apache2, 2018, apache/pdfbox Authors ( https://github.com/apache/pdfbox) 
//MIT, 2018-present, WinterDev  
```

Sources: [Typography.OpenFont/Tables.CFF/Type2CharStringParser.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/Type2CharStringParser.cs)

```text
//Apache2, 2018, Villu Ruusmann , Apache/PdfBox Authors ( https://github.com/apache/pdfbox)  
//Apache2, 2018-present, WinterDev 
```

Sources: [Typography.OpenFont/Tables.CFF/Type2InstructionCompacter.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.CFF/Type2InstructionCompacter.cs)

```text
//MIT, 2020-present, WinterDev  
```

Sources: [Typography.OpenFont/Tables.Others/VerticalMetricsHeader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.Others/VerticalMetricsHeader.cs)

```text
//Apache2, 2017-present, WinterDev
//https://www.microsoft.com/typography/otspec/vhea.htm
```

Sources: [Typography.OpenFont/Tables.TrueType/Cvt_Programs.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.TrueType/Cvt_Programs.cs)

```text
//MIT, 2015-2016, Michael Popoloski, WinterDev
```

Sources: [Typography.OpenFont/Tables.TrueType/GlyphLocations.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables.TrueType/GlyphLocations.cs)

```text
//MIT, 2018-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev
//https://www.microsoft.com/typography/otspec/loca.htm
```

Sources: [Typography.OpenFont/Tables/CharacterMap.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/CharacterMap.cs)

```text
//Apache2, 2017-present, WinterDev, Sam Hocevar
//Apache2, 2014-2016, Samuel Carlsson, WinterDev
```

Sources: [Typography.OpenFont/Tables/Utils.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/Tables/Utils.cs)

```text
//Apache2, 2017-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev 
```

Sources: [Typography.OpenFont/TrueTypeInterperter/InvalidFontException.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/TrueTypeInterperter/InvalidFontException.cs), [Typography.OpenFont/TrueTypeInterperter/TrueTypeInterpreter.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/TrueTypeInterperter/TrueTypeInterpreter.cs)

```text
//MIT, 2015, Michael Popoloski's SharpFont
```

Sources: [Typography.OpenFont/WebFont/Woff2Reader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/WebFont/Woff2Reader.cs)

```text
//MIT, 2019-present, WinterDev 
```

Sources: [Typography.OpenFont/WebFont/WoffReader.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.OpenFont/WebFont/WoffReader.cs)

```text
//MIT, 2019-present, WinterDev 
//see https://www.w3.org/TR/2012/REC-WOFF-20121213/
```

Sources: [Typography.TextBreak/Typography.TextBreak/BreakBounds.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/BreakBounds.cs), [Typography.TextBreak/Typography.TextBreak/BreakEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/BreakEngine.cs)

```text
//MIT, 2016-present, WinterDev
//some code from ICU project with BSD license
```

Sources: [Typography.TextBreak/Typography.TextBreak/BreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/BreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/CustomBreaker.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/CustomBreaker.cs), [Typography.TextBreak/Typography.TextBreak/CustomBreakerBuilder.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/CustomBreakerBuilder.cs), [Typography.TextBreak/Typography.TextBreak/CustomDict.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/CustomDict.cs), [Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/BurneseBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/BurneseBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/CJBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/CJBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/KhmerDictionaryBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/KhmerDictionaryBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/LaoDictionaryBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/LaoDictionaryBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/ThaiDictionaryBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/DictionaryBasedBreakingEngine/ThaiDictionaryBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/EngBreakingEngine.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/EngBreakingEngine.cs), [Typography.TextBreak/Typography.TextBreak/WordVisitor.cs](https://raw.githubusercontent.com/LayoutFarm/Typography/d331968f3ee714332a8ea4fa95a21c15812a8d93/Typography.TextBreak/Typography.TextBreak/WordVisitor.cs)

```text
//MIT, 2016-present, WinterDev
// some code from icu-project
// © 2016 and later: Unicode, Inc. and others.
// License & terms of use: http://www.unicode.org/copyright.html#License
```
