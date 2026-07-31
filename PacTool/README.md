# pactool

Unpacker, repacker and content converter for `CAPR` (`.pac`) archives from **Mega Man Network
Transmission** / *Rockman.EXE Transmission* (GameCube, `GREJ08`).

The archive format was recovered from the 203 archives in `files.7z` and cross-checked against the
original developer build scripts left on the disc (`pack.bat`, `pack.lst`, which invoked
`RockmanFilePack <list.lst> <out.pac>`). The payload formats were recovered from the same data,
from the `RockmanPicConv` sources under `sdk/`, and from `mmnt_pac_extract_full.py`.

## Archive format

An archive is a **flat chain of chunks**. There is no central directory, no file count and no
compression: each chunk is a 32-byte big-endian header followed immediately by its payload, and
the next chunk begins right after that payload. The chain ends at end of file.

```
0x00  char[4]   magic      "CAPR"
0x04  u32       size       payload length in bytes
0x08  u32       reserved0  always 0
0x0C  u32       reserved1  always 0
0x10  char[16]  name       ASCII, NUL-padded (no terminator when exactly 16 chars)
0x20  u8[size]  payload
```

Verified invariants across all 203 archives:

| Property | Observation |
| --- | --- |
| Magic | `CAPR` on every chunk, chain always lands exactly on EOF |
| `reserved0` / `reserved1` | `0` in every member |
| `size` | a multiple of 32 in every member |
| Member names | base names only — the packer stripped the directory part of each list entry |
| Name uniqueness | **not** unique; `PackTest.pac` stores `EnvMap.pcp` twice |
| Members per archive | 1 to 89 (`StageData/Fixed.pac`) |

## Build

```
dotnet build -c Release          # produces bin/Release/net8.0/pactool
dotnet test                      # 152 tests, from the solution root
```

Targets .NET 8. The tool itself has **no external dependencies** — the GX texture decoders and the
PNG encoder are part of the project, so nothing has to be restored to convert a texture. Only the
test project pulls in xunit.

## Usage

```
pactool list   <archive.pac> [--json]
pactool unpack <archive.pac> [-o <dir>] [--decode] [--no-manifest] [--strict]
pactool pack   <input> <archive.pac> [--align <n>] [--no-align]
pactool verify <archive.pac> [<archive.pac> ...] [--strict]
pactool decode <file> [<file> ...] [-o <dir>] [--mips] [--raw] [--flat]
pactool info   <file> [<file> ...]
```

`list`, `unpack`, `pack` and `verify` treat payloads as opaque bytes and round-trip them exactly.
`decode` and `info` look inside.

### Unpack and pack

```
$ pactool unpack files/StageData/Fixed.pac -o Fixed
       316,928  a01xxxxx.mpc
       177,920  a02xxxxx.mpc
       ...
Extracted 89 member(s) to .../Fixed
```

Alongside the payloads it writes two rebuild descriptions:

- **`pac.json`** — the authoritative one. Records member order, the *stored* name separately from
  the *extracted file* name, and the reserved header fields, so a rebuild is byte-exact even when
  names collide or contain characters the filesystem rejects.
- **`<archive>.lst`** — a `RockmanFilePack`-style list, for compatibility with the original
  toolchain and for hand-editing.

`pack` accepts three kinds of input:

| Input | Behaviour |
| --- | --- |
| `pac.json` | Exact rebuild. Order, stored names and reserved fields come from the manifest. |
| `*.lst` | One path per line, relative to the list. Nested `.lst` files are **inlined recursively**; the stored name is each file's base name. Blank lines and `#` comments are skipped. |
| a directory | Every file directly inside it, ordinal sorted by name (`pac.json` and `.lst` excluded). |

Payloads are padded with zeros up to a multiple of **32 bytes** by default, matching the invariant
above; the stated `size` includes the padding. Use `--no-align` for exact lengths, or `--align <n>`.

### Decode

`decode` turns the payloads into things you can open. Output goes into one directory per item.

```
$ pactool decode files/StageData/Stage0b.pac -o out
Stage0b.pac: CAPR archive, 22 member(s)
  map.dat              Map directory, 36 entries
  bg.dat               Background directory, 8 entries
  enemy.dat            Enemy directory, 4 entries, 24 spawn(s)
  b0bdfblk.scn         Picture Pack, 20 texture(s) + 234,208 B scene table
    79 shape(s), 79 decoded to geometry, 8,912 triangle(s)
  b0bstage.scn         Picture Pack, 3 texture(s) + 23,456 B scene table
    9 shape(s), 9 decoded to geometry, 1,352 triangle(s)
  l0blight.pcp         Picture Pack, 12 texture(s)
  d2dxxxxx.mpc         MPC model, 6 node(s), root 'chn5', 3,536 B mesh data
  ...
Wrote 202 file(s) to .../out
  51 image(s) and 88 mesh(es) decoded, 0 item(s) copied verbatim
```

| Input | Output |
| --- | --- |
| `.pac` | each member, decoded into its own directory |
| `.pcp` / `.scn` | `textures/*.png` and `textures/textures.txt`; for `.scn`, `scene.txt`, `scene.bin`, `geometry/*.obj` and one combined `<name>.obj` |
| `.mpc` | `skeleton.txt`, `skeleton.bin`, `mesh.bin` |
| `map.dat` / `bg.dat` / `enemy.dat` | `directory.txt` and `leveldata.bin` |
| `.bmd` / `.bdl` | `model.txt`, `sections/*.bin`, `textures/*.png` |
| `.bti` | `<name>.png` |
| `.pic` | `<name>.png` |
| anything else | copied out verbatim |

`--mips` writes every mip level (`tex.png`, `tex.mip1.png`, …) rather than only the base one;
`--raw` keeps the stored bytes too — each texture's, and each display list's. Yaz0-compressed
input is decompressed before it is identified. `unpack --decode` does both jobs at once, putting the converted files in
`<dir>/decoded/` so that packing the unpack directory back up is unaffected.

`info` prints the same analysis without writing anything:

```
$ pactool info files/Card/card.pac
files/Card/card.pac  (23,168 bytes)
  CAPR archive, 1 member(s)
    card.pcp               23,136 B
      Picture Pack, 9 texture(s)
        gmembanx         RGB5A3   96x32            wrap clamp/clamp      6,144 B
        gmemico0         RGB5A3   32x32            wrap clamp/clamp      2,048 B
        ...
```

## Payload formats

### GX textures

Every texture in every one of these containers is in a GameCube GX format, and they all share one
decoder. All eleven formats are implemented: `I4`, `I8`, `IA4`, `IA8`, `RGB565`, `RGB5A3`, `RGBA8`,
`C4`, `C8`, `C14X2` and `CMPR`, with `IA8`, `RGB565` and `RGB5A3` palettes for the indexed ones.
Images are stored as whole tiles, so dimensions are rounded up to the tile size and the padding is
discarded on the way out.

`CMPR` is DXT1 with two GameCube differences: the endpoint colours are big-endian, and the two-bit
indices in each row byte run from the most significant pair down. An 8×8 tile holds four 4×4 blocks
in row-major order.

Mip chains are sized level by level, each level padded up to whole tiles independently. That is why
an 8×8 `CMPR` chain of four levels is 128 bytes rather than the 42.7 a naive quarter-each-time sum
predicts. The formula reproduces the stored payload length of **all 3048 textures** in the
reference data exactly, which is what pins the format numbering down (see below).

### Picture Pack (`.pcp`, `.scn`)

The output of the developer tool `RockmanPicConv`. A `.scn` is the same container with a scene
description appended after the last texture.

```
0x00  u32        tableSize   bytes of texture data, i.e. where the scene table starts
0x04  u32        count
0x20  ...        texture[count], each a 0x40 header followed by its mip chain

texture header:
0x00  char[16]   name
0x10  u32        format      GX texture format
0x14  u32        dataSize
0x18  u16        width
0x1A  u16        height
0x1C  u32        wrapS       0 clamp, 1 repeat
0x20  u32        wrapT
0x24  u32        minFilter   1 linear, 5 trilinear (and therefore mipmapped)
0x28  u32        magFilter
0x2C  u32        lodBias
0x31  u8         minLod
0x32  u8         maxLod      the chain holds maxLod + 1 levels
```

The scene table of a `.scn` is a run of 32-byte records, classified by shape rather than by name:
a **section header** (a count followed by thirty zero bytes), a **named entry** (a name at 0x04,
plus an RGBA colour at 0x14 on material entries), or a **shape** — a named entry whose remaining
fields describe the GX display list stored immediately after it:

```
0x16  u16  streamBytes    bytes of display list following this record, padding included
0x1A  u16  listBytes      streamBytes - 32
0x1C  u16  vertexCount
0x1E  u16  triangleCount
```

Those fields make the table walkable exactly rather than by scanning, since a shape's display list
is skipped by its own stated length. They are also what identifies the vertex layout — see below.

### GX display lists and vertex layouts

A shape's geometry is a display list: the byte stream the graphics FIFO consumes. `fbarir.scn`'s
single shape begins `99 00 04` — opcode `0x98` is a triangle strip, its low three bits select
vertex attribute table 1, and `0x0004` is the vertex count. Every primitive in the shipped data is
a triangle strip. `pactool` implements the full opcode set anyway (NOP, CP/XF/BP register loads,
indexed loads, `CALL DL`, and all eight primitive shapes) because a stray unhandled opcode would
silently corrupt a walk rather than fail it.

**Where the vertex layout comes from.** On hardware a vertex's length and contents are not in the
stream — they are in the command processor's vertex descriptor (registers `0x50`/`0x60`) and
vertex attribute table (`0x70`/`0x80`/`0x90`), which the game writes before calling the list. The
shipped display lists contain no register loads of their own, so the descriptors are in the game
executable, not in these files. `pactool` recovers them from the data instead. Two layouts occur,
differing only in the texture coordinate:

| | Position | Normal | Texture coordinate | Size |
| --- | --- | --- | --- | --- |
| A | `s16[3]`, 8-bit fraction | `s8[3]`, 6-bit fraction | `s16[2]`, 8-bit fraction | 13 bytes |
| B | `s16[3]`, 8-bit fraction | `s8[3]`, 6-bit fraction | `f32[2]` | 17 bytes |

The normal's fraction is not a guess and not a VAT field: the hardware fixes it at 6 bits for a
signed byte. Under that reading **all 40,806 normals** in the reference set come out unit length to
within half a percent. The position fraction of 8 puts the models at roughly 100 units across,
which matches the placement coordinates in `map.dat`; a different fraction would put them in the
thousands or the hundredths.

**Choosing between the two layouts**, per shape, in order of how much each test is trusted:

1. The walk has to reach exactly the vertex and triangle counts the shape record declares. A wrong
   vertex length lands on a different count or overruns the stream. This alone settles **6907 of
   the 7902** shapes in the reference data.
2. A display list is padded up to a multiple of 32 bytes and the record states the padded length,
   so the bytes consumed must round up to it. That settles another **981** — the short lists, where
   padding had been hiding the difference.
3. Failing those, the decoded values: a normal that is not unit length, or a texture coordinate
   that comes out as a denormal or in the thousands, means the layout is wrong. That settles the
   last **10**.

**4 shapes** are left that no layout reproduces (`atgallM3`, `obj7M0`, and `jpihex1M` twice); their
byte counts are not consistent with any whole vertex size. They are reported and skipped rather
than guessed at.

Geometry is exported as Wavefront OBJ — one file per shape under `geometry/`, plus one combined
file per container. Vertices are written exactly as the stream stores them, with no welding, so
the file stays a record of the display list rather than an interpretation of it.

**Character models are different.** The shape records in `c*xxxxx.scn` and `d*xxxxx.scn` set the
word at 0x00 to 1 and carry no display list; their geometry is in some other, non-FIFO form that
this tool does not decode. The `.mpc` mesh blob is likewise not a display list — no run of GX
primitive opcodes appears anywhere in any of the 119 sampled files. Both are extracted verbatim.

### MPC models (`.mpc`)

A skeleton followed by packed mesh data. Entries are 24 bytes and appear in depth-first order; the
type byte both names the node's role — root bone, chain connector, mesh part, effect node — and
fixes its depth, so the tree is recoverable from the flat sequence. Mesh parts carry an offset into
the mesh blob in 16-bit words. The header at 0x08 is an entry-shaped record naming the root chain.

### Stage directories (`map.dat`, `bg.dat`, `enemy.dat`)

A size, a count, an offset table, then variable-length entries. `map.dat` and `bg.dat` share an
entry format: a 20-byte head with a name and a placement count, then that many 40-byte placements,
each a kind and four floats. `bg.dat` only names assets, so its counts are always zero and its
entries always 20 bytes. An `enemy.dat` entry is a spawn count followed by 24-byte records, each a
three-character model reference and five floats.

Both sizes are confirmed against the data rather than assumed: `20 + placements * 40` reproduces
the stored length of **all 1373** map and background entries, and `4 + spawns * 24` **all 82** enemy
entries. Everything past the directory is the level data proper, a separate format extracted
verbatim to `leveldata.bin`.

### J3D models (`.bmd`, `.bdl`)

Nintendo's GameCube model format. `bmd3` and `bdl4` differ only in the extra `MDL3` section of
pre-compiled draw calls, so one reader handles both. Every section is inventoried and extracted
verbatim; `TEX1` textures are decoded to PNG through the shared GX decoder, `INF1` becomes a
hierarchy listing, and the names in `JNT1` and `MAT3` are resolved against it.

Several `TEX1` headers may point at one shared image, because each header's image and palette
offsets are relative to itself — that is handled. **Geometry is not reconstructed**: unlike the `.scn`
shapes, a BMD's vertex layout is stored in its own `VTX1` and `SHP1` sections rather than having to
be recovered, but nothing in this repository would exercise the code, so it is not written.

A standalone `.bti` is the same texture header at offset 0, and decodes the same way.

### Softimage PIC (`.pic`)

The source art the effect textures were authored from — 99 of them under
`StageData/fixed/Effect/`, all written by the same Photoshop plug-in. Channel packets carry RGB and
alpha separately, each scan line run-length encoded: a lead byte under 128 introduces that many
plus one literal pixels, 128 introduces a 16-bit run length, and anything above 128 is a run of
`lead - 127`. Reading it that way consumes all 99 files exactly to their last byte, which the
off-by-one alternatives do not.

## Where this disagrees with the references

Two of the sources in this repository describe these formats differently. Both disagreements were
settled against the shipped data.

**`sdk/include/rockman.h` numbers the GX formats sequentially from `IA4 = 0`.** The data does not
agree. `Card/card.pac` stores a 96×32 texture as format 5 in 6144 bytes, which is 2 bytes per pixel
and therefore `RGB5A3` under the hardware's own numbering, not `CI4` as that header would have it.
Under the hardware numbering the mip-chain formula reproduces all 3048 stored payload lengths
exactly; under the header's it reproduces none. The same applies to `sdk/pcp_parse.c`, which expects
a `PIC\0` magic and 16-byte descriptors that no shipped `.pcp` has.

**`mmnt_pac_extract_full.py` has three bugs that this implementation does not reproduce.**

1. *It truncates the last 32 bytes of every texture.* It computes the end of a `CAPR` block as
   `offset + size` without adding the 32-byte header, so the final 8×8 tile of each texture is
   dropped and decodes as black. Comparing against its own output: **141 of 141** PNGs from
   `Menu/echipall/` match this tool pixel for pixel, and every difference is confined to that one
   tile.
2. *It routes textures to the wrong extractor by name.* Sub-sections called `grd*`, `blk*`, `sec*`
   or `move*` are sent to a CSV grid writer or dumped as geometry. They are ordinary CMPR textures:
   `Stage0b.pac`'s `grdxxx` is 32×32 with six mip levels, and its 768 bytes match that exactly.
   This tool decodes them like any other texture.
3. *It misreads `map.dat` entries.* It takes the word at 0x10 as a type and the word at 0x14 as a
   float count, then reads floats past the end of the entry into its neighbour. 0x10 is a placement
   count and 0x14 is the first placement's kind.

## Round-trip and validation status

- `verify` over all 203 archives — **203/203 byte-identical**.
- `unpack --decode` then `pack` from `pac.json` on `Fixed.pac` — byte-identical; converted output
  lives in `decoded/` and does not disturb the rebuild.
- Texture decoding against `mmnt_pac_extract_full.py`'s own PNGs — **141/141**, differing only in
  the tile the reference truncates.
- Mip-chain sizing against every texture header in the reference data — **3048/3048**.
- `decode` over every `.pac`, `.pcp`, `.scn`, `.mpc` and `.pic` in `files.7z` — 3147 images and
  7898 meshes written, 12 items copied verbatim (the `playdemo*.dat` input recordings, whose format
  is not known), no crashes and 4 warnings, all of them the undecodable shapes named above.
- Geometry decoding — **7898/7902** shapes, 641,480 triangles. Spot checks hold up
  geometrically: `roomconv.scn`'s `cube1M0` has 24 vertices at exactly 8 distinct corners, and its
  `scballM0` is a sphere whose radius from the bounding-box centre varies only between 3.398 and
  3.404. Across a 600-file sample of the exported OBJs, **no normal** is off unit length.
- 152 unit tests. The J3D and Yaz0 fixtures are constructed rather than sampled, since the
  reference data contains neither.

## Notes and edge cases

- **Duplicate names.** Extraction disambiguates on disk (`EnvMap.pcp`, `EnvMap~2.pcp`) while
  `pac.json` keeps the original stored name for each. A warning tells you to rebuild from the
  manifest rather than the `.lst` in that case.
- **Untrusted names.** Names come from the archive, so path separators and `..` are replaced before
  anything is created on disk, for both members and the texture names inside them.
- **Long names.** The name field holds 16 bytes; packing a longer name is a hard error rather than
  a silent truncation.
- **Non-zero reserved fields** are preserved through a round trip and reported as a warning.
  `--strict` turns warnings into errors.
- **Damaged input** is reported and worked around rather than thrown away: a truncated mip chain
  decodes as far as it reaches, an over-long entry count is clamped, and a payload that fails to
  parse is written out verbatim so nothing is lost.
- Writes go to a temporary file that is moved into place, so an interrupted pack cannot leave a
  truncated archive behind.

## Layout

```
PacTool.csproj
src/PacFormat.cs                format constants and the exception type
src/PacMember.cs                one chunk as found on disk
src/PacArchive.cs               reader: walks the chain, streams payloads on demand
src/PacSource.cs                one member to be written, from a file or from memory
src/PacBuilder.cs               writer: headers, alignment padding, atomic replace
src/ListFile.cs                 .lst parsing with recursive inlining and cycle detection
src/Manifest.cs                 pac.json model
src/Program.cs                  CLI

src/Gx/GxTextureFormat.cs       GX formats: tile geometry, bit depth, mip chain sizes
src/Gx/GxImageDecoder.cs        all eleven GX texture formats to RGBA
src/Gx/GxPalette.cs             TLUT decoding
src/Gx/GxVertexFormat.cs        vertex descriptor and attribute table: sizes and offsets
src/Gx/GxDisplayList.cs         the FIFO opcode stream
src/Gx/GxVertexReader.cs        direct attributes out of a vertex

src/Imaging/Rgba32Image.cs      decoded image buffer
src/Imaging/PngWriter.cs        dependency-free PNG encoder

src/Formats/Ascii.cs            fixed-width name fields
src/Formats/ContentSniffer.cs   works out what a payload is
src/Formats/PicturePack.cs      .pcp / .scn texture container
src/Formats/SceneTable.cs       the scene description a .scn appends
src/Formats/ScnMesh.cs          shape geometry, and the layouts its display lists use
src/Formats/MpcModel.cs         .mpc skeleton and mesh data
src/Formats/DataDirectory.cs    map.dat / bg.dat / enemy.dat
src/Formats/J3dModel.cs         .bmd / .bdl
src/Formats/BtiTexture.cs       BTI texture header, standalone and inside TEX1
src/Formats/SoftimagePic.cs     .pic source art
src/Formats/Yaz0.cs             Nintendo's run-length compression

src/Extract/ContentExtractor.cs orchestration: what gets written where
src/Extract/ObjWriter.cs        Wavefront OBJ output
```

## References

The GX register layouts here — the vertex descriptor and attribute table bitfields, the opcode set,
and the per-attribute size rules — were taken from these and then checked against the data:

- [*GX Programming Manual*](https://www.davidgf.net/downloads/gcwii/gx.pdf) — the graphics
  processor's own documentation: command processor registers, the FIFO command set, texture formats.
- [*Nintendo GameCube Architecture Guide*](https://db.hfsplay.fr/files/2019/07/04/Architecture_Guide_SCQNknY.pdf)
  — how the pieces fit together, and where display lists sit in the pipeline.
- [Dolphin](https://github.com/dolphin-emu/dolphin) — `VideoCommon/CPMemory.h` for the exact
  bitfield positions, `VideoCommon/OpcodeDecoding.h` for the opcodes, and the `VertexLoader_*.h`
  size tables for what each attribute costs in each mode. Dolphin is the reason two apparent
  inconsistencies in this code are deliberate rather than bugs: `IA4` puts alpha in the high nibble
  while `IA8` puts intensity in the first byte, and a normal's fixed-point fraction comes from the
  hardware rather than from the attribute table.
