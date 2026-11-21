# Third-Party Notices

This project includes third-party software and assets. This file contains the
required attributions and license texts.

---

## Syzygy tablebase prober (WDL + DTZ)

**Source**: https://github.com/jdart1/Fathom (MIT License), plus Ronald de
Man's `tbprobe.c` (released "for unrestricted distribution and use").
**Authors**: Ronald de Man (2013-2018), basil00 (2015), Jon Dart (2016-2024).
**License**: MIT (see below)

`src/PawnForge.Core/Tb/Syzygy.cs` is a C# port of the Syzygy tablebase
probing code (`tbprobe.c` + `tbchess.c`) from jdart1/Fathom, covering the
`.rtbw` (WDL) and `.rtbz` (DTZ) formats, the Pairs bit-compression
decompression, and the WDL/DTZ capture + pawn-move resolution. The MIT
license permits use in PawnForge's MIT-licensed engine.

```
MIT License

Copyright (c) 2013-2018 Ronald de Man
Copyright (c) 2015 basil00
Modifications Copyright (c) 2016-2024 Jon Dart

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Test tablebases** (`tests/PawnForge.Tests/tbdata/*`): a subset of the free
public 3-man Syzygy tablebases (KQvK, KRvK, KPvK; .rtbw + .rtbz), hosted by
Lichess at https://tablebase.lichess.org/ and redistributable under Ronald
de Man's licensing. Used only by the unit tests.

---

## Embedded neural network

`src/PawnForge.Core/Resources/jokausch.nnue` is a network trained for this
project. It is distributed under the same MIT license as the engine.

---

## ChessBase CTG book format

The `.ctg` opening-book reader (`src/PawnForge.Core/Book/CtgBook.cs`,
`CtgSignature.cs`) is an original C# implementation written from the *public
documentation* of the CTG format (originally reverse-engineered and published
by Stephan Vermeire for the Brutus engine, later restated on
chessprogramming.org). The Huffman code table and the opaque move-byte →
piece/delta tables are format constants that any CTG reader must contain; the
surrounding code is PawnForge's own. No CTG source code was copied. Books
themselves (e.g. `books/Perfect2023.ctg`) are third-party and NOT
redistributed.

---

## Format and protocol references

- Syzygy tablebase format and probing documentation:
  https://syzygy-tables.info/ and https://chessprogramming.org/Syzygy_Bases
- Universal Chess Interface (UCI) protocol: as documented on
  https://chessprogramming.org/UCI

These are public documentation sources; no code from them is included beyond
original implementations.

---

*If you are the rights holder of any component listed here and believe the
attribution is incomplete or incorrect, please open an issue.*
