/*
 * RockmanPicConv.exe  —  Decompiled / Reconstructed Source
 * ============================================================
 * Original binary  : RockmanPicConv.exe  (PE32 / i386)
 * Compiled with    : Microsoft Visual C++ 6.0  (Debug build)
 * PE timestamp     : Fri Feb  8 11:33:00 2002
 * PDB path         : D:\!cur\Win\RockmanPicConv\Debug\RockmanPicConv.pdb
 * Known source     : d:\!cur\win\rockmanpicconv\source\tga\tga.cpp
 *
 * Product          : RockmanEXE Cube (Mega Man Battle Network: GameCube)
 * Publisher        : Capcom Co., Ltd.
 * Function         : Batch-converts TGA image files into a Capcom PCP
 *                    (ピクチャパック / Picture Pack) file for the GameCube,
 *                    driven by a text-based TPL script file.
 *
 * Reconstruction method:
 *   - PE import table analysis (objdump -p)
 *   - radare2 function-list analysis (aaa; afl)
 *   - objdump -d disassembly of entry point, main, and all major functions
 *   - String extraction from .text, .rdata, and .data sections, including
 *     the original Japanese UI strings embedded in the binary
 *   - Error-message strings carry original function names verbatim
 *
 * Usage:
 *   RockmanPicConv  <script_file>  <out_pcp_file>
 *
 *   <script_file>  — スクリプトファイル (text TPL script)
 *   <out_pcp_file> — 出力ピクチャパックファイル (binary PCP output)
 *
 * Output format note:
 *   The produced .pcp file is a GameCube TPL (Texture Pack List) file with
 *   a Capcom-specific outer wrapper ("PIC" magic / PICReader).
 *   Textures are stored in standard GX pixel formats; palettes in RGB565 or
 *   RGB5A3.  All multi-byte values are big-endian (GameCube native).
 *
 * Reconstruction accuracy: HIGH for structure, function signatures, and
 *   data formats; APPROXIMATE for internal loop logic in larger functions
 *   where only the disassembly was available.
 * ============================================================
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

/* ─────────────────────────────────────────────────────────────────
 *  GameCube GX texture / wrap-mode enumerations
 *  (mirrored from the GX SDK; values match the table at VA 0x456700)
 * ───────────────────────────────────────────────────────────────── */

typedef enum {
    GX_TF_IA4     = 0,   /* "IA4"     — 4-bpp intensity+alpha               */
    GX_TF_IA8     = 1,   /* "IA8"     — 8-bpp intensity+alpha               */
    GX_TF_RGB565  = 2,   /* "RGB565"  — 16-bpp, no alpha                    */
    GX_TF_RGB5A3  = 3,   /* "RGB5A3"  — 16-bpp, 3-bit alpha                 */
    GX_TF_RGBA8   = 4,   /* "RGBA8"   — 32-bpp full RGBA                    */
    GX_TF_CI4     = 5,   /* "CI4"     — 4-bpp colour-indexed                */
    GX_TF_CI8     = 6,   /* "CI8"     — 8-bpp colour-indexed                */
    GX_TF_CI14X2  = 7,   /* "CI14_X2" — 14-bpp colour-indexed (extended)    */
    GX_TF_CMPR    = 8,   /* "CMPR"    — S3TC/DXT1 compression               */
    GX_TF_INVALID = -1
} GXTexFmt;

typedef enum {
    GX_CLR_RGB565 = 0,   /* "RGB565"  — palette entry format */
    GX_CLR_RGB5A3 = 1,   /* "RGB5A3"  — palette entry format */
    GX_CLR_IA8    = 2,   /* "IA8"     — palette entry format */
    GX_CLR_INVALID = -1
} GXTlutFmt;

typedef enum {
    GX_CLAMP  = 0,       /* "GX_CLAMP"  — clamp to border   */
    GX_REPEAT = 1,       /* "GX_REPEAT" — tile              */
    GX_MIRROR = 2,       /* "GX_MIRROR" — mirror-tile       */
    GX_WRAP_INVALID = -1
} GXTexWrapMode;

/* Texture format string table (indexed by GXTexFmt; lives at VA 0x456700) */
static const char *g_texFmtNames[] = {
    "IA4", "IA8", "RGB565", "RGB5A3", "RGBA8", "CI4", "CI8", "CI14_X2", "CMPR", NULL
};

/* Wrap-mode string tables (two arrays, wrapS and wrapT; VA 0x456740 / 0x456780) */
static const char *g_wrapModeNames[] = {
    "GX_CLAMP", "GX_REPEAT", "GX_MIRROR", NULL
};


/* ─────────────────────────────────────────────────────────────────
 *  TGA file-format structures
 *  (Truevision TARGA; source file: tga.cpp)
 * ───────────────────────────────────────────────────────────────── */

#pragma pack(push, 1)

typedef struct {
    unsigned char  idLength;         /* byte 0  — length of image ID field  */
    unsigned char  colorMapType;     /* byte 1  — 0=none, 1=present         */
    unsigned char  imageType;        /* byte 2  — see TGA_IMAGE_TYPE_*      */
    /* Color-map specification */
    unsigned short colorMapOrigin;   /* byte 3-4  — first entry index       */
    unsigned short colorMapLength;   /* byte 5-6  — number of entries       */
    unsigned char  colorMapDepth;    /* byte 7    — bits per entry          */
    /* Image specification */
    unsigned short xOrigin;          /* byte 8-9  — X pixel coordinate      */
    unsigned short yOrigin;          /* byte 10-11 — Y pixel coordinate     */
    unsigned short width;            /* byte 12-13 — image width in pixels  */
    unsigned short height;           /* byte 14-15 — image height in pixels */
    unsigned char  pixelDepth;       /* byte 16   — bits per pixel          */
    unsigned char  imageDescriptor;  /* byte 17   — image origin, alpha bits*/
} TgaHeader;  /* 18 bytes */

#pragma pack(pop)

/* TGA image-type codes */
#define TGA_TYPE_NO_IMAGE          0
#define TGA_TYPE_COLOR_MAPPED      1   /* palette / colour-indexed           */
#define TGA_TYPE_TRUE_COLOR        2   /* RGB                                */
#define TGA_TYPE_GRAYSCALE         3   /* intensity (no colour)              */
#define TGA_TYPE_RLE_COLOR_MAPPED  9   /* RLE-compressed variants            */
#define TGA_TYPE_RLE_TRUE_COLOR   10
#define TGA_TYPE_RLE_GRAYSCALE    11

/* Internal per-image data block produced by DecodeTgaFile().
 * Exact field layout inferred from disassembly offsets (+0x14, +0x18, +0x1c …).
 *
 * The structure sits 0x20 bytes before the raw-data pointer returned by
 * CreateImageColorLayerFromTga (the "block" offset used in DecodeTgaFile
 * is [ptr - 0x20]).
 */
typedef struct TgaImageBlock {
    /* +0x00 */ unsigned char  *rawBits;       /* allocated pixel buffer     */
    /* +0x04 */ unsigned int    rawSize;        /* size of rawBits in bytes   */
    /* +0x08 */ unsigned int    dfPtr;          /* offset / deferred ptr      */
    /* +0x0c */ unsigned int    reserved0;
    /* +0x10 */ unsigned int    reserved1;
    /* +0x14 */ unsigned int    imageType;      /* TGA image-type code        */
    /* +0x18 */ unsigned short  width;
    /* +0x1a */ unsigned short  height;
    /* +0x1c */ unsigned char   pixelDepth;
    /* +0x1d */ unsigned char   colorMapDepth;  /* bits per palette entry     */
    /* +0x1e */ unsigned char   imageDescriptor;
    /* +0x1f */ unsigned char   paletteOffset;  /* current palette base idx   */
    /* +0x20 */ unsigned char  *colorLayer;     /* output of CreateImageColor  */
    /* +0x24 */ unsigned char  *alphaLayer;     /* output of CreateImageAlpha  */
    /* … */
} TgaImageBlock;


/* ─────────────────────────────────────────────────────────────────
 *  PCP / PICReader structures
 *  Capcom Picture-Pack file format ("PCP"); magic = "PIC\0"
 *  Contains a GameCube TPL (Texture Pack List) with a short outer header.
 * ───────────────────────────────────────────────────────────────── */

#define PCP_MAGIC  0x50494300u    /* "PIC\0" big-endian                      */

/* PCP / TPL script-file keywords (parsed by TCReadTplTextFile) */
#define SCRIPT_KW_PATH    "PATH"
#define SCRIPT_KW_NULL    "NULL"
#define SCRIPT_KW_FILE    "FILE"
#define SCRIPT_KW_IMAGE   "IMAGE"
#define SCRIPT_KW_PALETTE "PALETTE"
#define SCRIPT_KW_TEXTURE "TEXTURE"

/* Per-image entry parsed from the script */
typedef struct TplImageEntry {
    char           tgaPath[512];       /* resolved path to TGA source file   */
    GXTexFmt       texFmt;             /* output GX texture format           */
    GXTlutFmt      palFmt;             /* palette format (CI* types only)    */
    GXTexWrapMode  wrapS;              /* horizontal wrap                    */
    GXTexWrapMode  wrapT;             /* vertical wrap                      */
    int            paletteIndex;       /* index into g_paletteTable          */
    unsigned short width;
    unsigned short height;
    unsigned char *colorData;          /* converted pixel data               */
    unsigned char *alphaData;          /* alpha channel (separate layer)     */
    int            numMips;            /* mip-map count (1 = no mips)        */
} TplImageEntry;

/* Per-palette entry */
typedef struct TplPaletteEntry {
    char           tgaPath[512];
    GXTlutFmt      palFmt;            /* RGB565 or RGB5A3                   */
    int            numColors;          /* number of palette entries          */
    unsigned char *palData;            /* converted palette pixel data       */
} TplPaletteEntry;

/* Top-level script/TPL state — one per invocation */
typedef struct TplState {
    char              basePath[512];   /* PATH keyword value                 */
    int               numImages;       /* FILE count                         */
    int               numPalettes;
    TplImageEntry    *images;
    TplPaletteEntry  *palettes;
    int               version;         /* TPL file version                   */
} TplState;


/* ─────────────────────────────────────────────────────────────────
 *  Forward declarations
 * ───────────────────────────────────────────────────────────────── */

/* tga.cpp */
static int  DecodeTgaFile(const char *filename, TgaImageBlock **outBlock);
static unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block,
                                                   const char    *filename);
static unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block,
                                                   const char    *filename);
static void AdjustTgaForPaletteOffset(TgaImageBlock *block);

/* TPL / PCP */
static int  TCReadTplTextFile(TplState *state, const char *scriptPath);
static int  TCWriteTplFile(TplState *state, const char *outPcpPath);
static int  TCSetTplPaletteValues(TplState *state, int paletteIdx);
static int  TCGetTplVersion(const char *existingTplPath);
static int  WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx);
static int  WritePaletteBlockRGB5A3(FILE *fp, TplPaletteEntry *pal, int paletteIdx);

/* Helpers */
static GXTexFmt      ParseTexFmt(const char *s);
static GXTlutFmt     ParseTlutFmt(const char *s);
static GXTexWrapMode ParseWrapMode(const char *s);
static int           IsPowerOfTwo(unsigned int x);


/* ─────────────────────────────────────────────────────────────────
 *  main()
 *  Entry: main(int argc, char **argv)  — thunk at 0x4010ff → 0x4012c0 → 0x402970
 * ───────────────────────────────────────────────────────────────── */

int main(int argc, char **argv)
{
    /* Banner / description (originally in Japanese + ASCII):
     *   "RockmanEXE Cube ピクチャコンバータ \"RockmanPicConv\"\n"
     *   (RockmanEXE Cube Picture Converter "RockmanPicConv")
     * Strings at .rdata VA 0x44f2cc and 0x44f384.
     */
    printf("=============================================\n");
    printf("RockmanEXE Cube Picture Converter \"RockmanPicConv\"\n");
    printf("=============================================\n");

    if (argc < 3) {
        /* Japanese help text followed by ASCII usage (VA 0x44f314, 0x44f358, 0x44f384) */
        printf("usage : RockmanPicConv <script file> <out pcp file>\n");
        printf("  <script file>  -- TPL script file\n");
        printf("  <out pcp file> -- output Picture Pack file\n");
        return 1;
    }

    const char *scriptPath  = argv[1];
    const char *outPcpPath  = argv[2];

    /* Normalise path separators: replace '/' with '\\' and vice-versa.
     * The disassembly at 0x4029c7-0x4029e8 pushes 0x5c ('\') and 0x2f ('/')
     * as replacement pairs before calling a string-replace helper. */

    TplState state;
    memset(&state, 0, sizeof(state));

    /* Read and parse the TPL text script */
    if (TCReadTplTextFile(&state, scriptPath) != 0) {
        fprintf(stderr, "Error: failed to read script file '%s'\n", scriptPath);
        return 1;
    }

    /* Print output summary (disassembly at 0x402aec-0x402b42):
     *   "O = %d\n"  — number of images
     *   "Y : %d バイト\n"  — total byte count (rounded up to 1K) */
    int totalBytes = 0;
    for (int i = 0; i < state.numImages; i++) {
        totalBytes += state.images[i].width * state.images[i].height * 4;
    }
    printf("O = %d\n", state.numImages);
    printf("Y : %d bytes\n", (totalBytes + 0x3FF) >> 10);  /* display in KB */

    /* Write the converted PCP file */
    if (TCWriteTplFile(&state, outPcpPath) != 0) {
        fprintf(stderr, "Error: failed to write PCP file '%s'\n", outPcpPath);
        /* clean up and fall through */
    }

    /* Free resources */
    if (state.images) {
        for (int i = 0; i < state.numImages; i++) {
            free(state.images[i].colorData);
            free(state.images[i].alphaData);
        }
        free(state.images);
    }
    if (state.palettes) {
        for (int i = 0; i < state.numPalettes; i++) {
            free(state.palettes[i].palData);
        }
        free(state.palettes);
    }

    return 0;
}


/* ─────────────────────────────────────────────────────────────────
 *  TCReadTplTextFile()
 *  Parses the plain-text TPL script that drives the conversion.
 *
 *  Script grammar (inferred from keyword table at VA 0x453318):
 *
 *    PATH    <base_directory>
 *    FILE    <total_image_count>
 *    IMAGE   <n>                        -- begin image block n
 *    PALETTE <palette_index>            -- (optional) indexed-colour source
 *    TEXTURE <tga_filename>  <fmt>  <wrapS>  <wrapT>
 *    NULL                               -- empty/placeholder slot
 *
 *  The parser uses TCReadTplTextFile's line-limit check:
 *    "TCReadTplTextFile: line exceeded %d characters in '%s'"
 * ───────────────────────────────────────────────────────────────── */

#define SCRIPT_MAX_LINE  1024   /* maximum characters per line */

static int TCReadTplTextFile(TplState *state, const char *scriptPath)
{
    FILE *fp = fopen(scriptPath, "r");
    if (!fp) {
        fprintf(stderr, "TCReadTplTextFile: couldn't open %s for read\n", scriptPath);
        return -1;
    }

    char line[SCRIPT_MAX_LINE + 2];
    int  lineNum     = 0;
    int  imageIdx    = -1;
    int  paletteIdx  = -1;
    int  allocImages = 0;
    int  allocPals   = 0;

    while (fgets(line, sizeof(line), fp)) {
        lineNum++;

        /* The tool checks the line length before processing */
        if ((int)strlen(line) > SCRIPT_MAX_LINE) {
            fprintf(stderr, "TCReadTplTextFile: line exceeded %d characters in '%s'\n",
                    SCRIPT_MAX_LINE, scriptPath);
            fclose(fp);
            return -1;
        }

        /* Strip trailing newline / carriage-return */
        char *nl = strpbrk(line, "\r\n");
        if (nl) *nl = '\0';

        /* Skip blank lines and comments */
        if (line[0] == '\0' || line[0] == '#' || line[0] == ';')
            continue;

        char keyword[64];
        if (sscanf(line, "%63s", keyword) != 1)
            continue;

        /* ── PATH ─────────────────────────────────────────── */
        if (strcmp(keyword, SCRIPT_KW_PATH) == 0) {
            sscanf(line, "%*s %511s", state->basePath);
            continue;
        }

        /* ── NULL (empty placeholder) ─────────────────────── */
        if (strcmp(keyword, SCRIPT_KW_NULL) == 0) {
            /* increment image counter but leave entry zeroed */
            if (imageIdx >= 0 && imageIdx < state->numImages)
                imageIdx++;
            continue;
        }

        /* ── FILE <count> ─────────────────────────────────── */
        if (strcmp(keyword, SCRIPT_KW_FILE) == 0) {
            int n;
            if (sscanf(line, "%*s %d", &n) == 1 && n > 0) {
                state->numImages = n;
                state->images    = (TplImageEntry *)calloc(n, sizeof(TplImageEntry));
                if (!state->images) {
                    fclose(fp);
                    return -1;
                }
                allocImages = n;
            }
            continue;
        }

        /* ── IMAGE <index> ─────────────────────────────────── */
        if (strcmp(keyword, SCRIPT_KW_IMAGE) == 0) {
            int idx;
            if (sscanf(line, "%*s %d", &idx) == 1)
                imageIdx = idx;
            continue;
        }

        /* ── PALETTE <index> ─────────────────────────────── */
        if (strcmp(keyword, SCRIPT_KW_PALETTE) == 0) {
            int idx;
            if (sscanf(line, "%*s %d", &idx) == 1) {
                paletteIdx = idx;

                /* Grow palette array if needed */
                if (idx >= allocPals) {
                    int newCap = idx + 16;
                    state->palettes = (TplPaletteEntry *)realloc(state->palettes,
                                      newCap * sizeof(TplPaletteEntry));
                    if (!state->palettes) { fclose(fp); return -1; }
                    memset(state->palettes + allocPals, 0,
                           (newCap - allocPals) * sizeof(TplPaletteEntry));
                    allocPals = newCap;
                }
                state->numPalettes = (idx + 1 > state->numPalettes)
                                   ? idx + 1 : state->numPalettes;

                /* Read palette tga + format from next tokens */
                char palFile[256], fmtStr[64];
                if (sscanf(line, "%*s %*d %255s %63s", palFile, fmtStr) == 2) {
                    snprintf(state->palettes[idx].tgaPath,
                             sizeof(state->palettes[idx].tgaPath),
                             "%s/%s", state->basePath, palFile);
                    state->palettes[idx].palFmt = ParseTlutFmt(fmtStr);
                }
            }
            continue;
        }

        /* ── TEXTURE <file> <fmt> <wrapS> <wrapT> ─────────── */
        if (strcmp(keyword, SCRIPT_KW_TEXTURE) == 0) {
            if (imageIdx < 0 || imageIdx >= allocImages) {
                fprintf(stderr, "TCReadTplTextFile: TEXTURE without IMAGE in '%s'\n",
                        scriptPath);
                continue;
            }
            TplImageEntry *img = &state->images[imageIdx];

            char texFile[256], fmtStr[64], wrapSStr[32], wrapTStr[32];
            if (sscanf(line, "%*s %255s %63s %31s %31s",
                       texFile, fmtStr, wrapSStr, wrapTStr) >= 2)
            {
                snprintf(img->tgaPath, sizeof(img->tgaPath),
                         "%s/%s", state->basePath, texFile);
                img->texFmt = ParseTexFmt(fmtStr);
                img->wrapS  = ParseWrapMode(wrapSStr);
                img->wrapT  = ParseWrapMode(wrapTStr);
                img->paletteIndex = paletteIdx;   /* last seen PALETTE index */
            }
            imageIdx++;
            continue;
        }
    }

    fclose(fp);
    return 0;
}


/* ─────────────────────────────────────────────────────────────────
 *  DecodeTgaFile()
 *  Reads and validates a TGA file into a TgaImageBlock.
 *  Source file: tga.cpp
 *  Original function: DecodeTgaFile()
 *  VA range: ~0x41c920 (177 blocks, 3108 bytes — largest non-CRT function)
 *
 *  Asserts observed in .rdata:
 *    "dfPtr != NULL"     at VA 0x4504f0
 *    "rawBits != NULL"   at VA 0x450500
 *    "rawSize > 0"       at VA 0x450514
 * ───────────────────────────────────────────────────────────────── */

static int DecodeTgaFile(const char *filename, TgaImageBlock **outBlock)
{
    assert(filename != NULL);

    FILE *fp = fopen(filename, "rb");
    if (!fp) {
        fprintf(stderr, "Error opening TGA: %s\n", filename);
        return 0;
    }

    TgaHeader hdr;
    if (fread(&hdr, sizeof(hdr), 1, fp) != 1) {
        fclose(fp);
        return 0;
    }

    /* Skip image ID field */
    if (hdr.idLength > 0)
        fseek(fp, hdr.idLength, SEEK_CUR);

    /* Validate: the file must contain image data or a palette */
    if (hdr.imageType == TGA_TYPE_NO_IMAGE && hdr.colorMapType == 0) {
        fprintf(stderr, "DecodeTgaFile(): file %s contains no image or palette data\n",
                filename);
        fclose(fp);
        return 0;
    }

    /* Validate palette descriptor for colour-mapped images */
    if (hdr.colorMapType == 1) {
        /* Supported palette bit depths: 15, 16, 24, 32 */
        if (hdr.colorMapDepth != 15 && hdr.colorMapDepth != 16 &&
            hdr.colorMapDepth != 24 && hdr.colorMapDepth != 32)
        {
            fprintf(stderr, "DecodeTgaFile(): unsupported palette bit depth in file %s\n",
                    filename);
            fclose(fp);
            return 0;
        }
        /* Supported palette entry sizes: same set */
        unsigned int entryBytes = (hdr.colorMapDepth + 7) / 8;
        if (entryBytes == 0 || entryBytes > 4) {
            fprintf(stderr, "DecodeTgaFile(): unsupported palette entry size in file %s\n",
                    filename);
            fclose(fp);
            return 0;
        }
    }

    /* Allocate block */
    TgaImageBlock *block = (TgaImageBlock *)calloc(1, sizeof(TgaImageBlock));
    if (!block) { fclose(fp); return 0; }

    block->imageType       = hdr.imageType;
    block->width           = hdr.width;
    block->height          = hdr.height;
    block->pixelDepth      = hdr.pixelDepth;
    block->colorMapDepth   = hdr.colorMapDepth;
    block->imageDescriptor = hdr.imageDescriptor;

    /* Read palette data (if present) */
    unsigned char *palData = NULL;
    if (hdr.colorMapType == 1 && hdr.colorMapLength > 0) {
        unsigned int entryBytes = (hdr.colorMapDepth + 7) / 8;
        unsigned int palBytes   = hdr.colorMapLength * entryBytes;
        palData = (unsigned char *)malloc(palBytes);
        if (!palData) { free(block); fclose(fp); return 0; }

        /* Skip to the first used palette entry */
        if (hdr.colorMapOrigin > 0)
            fseek(fp, hdr.colorMapOrigin * entryBytes, SEEK_CUR);

        if (fread(palData, palBytes, 1, fp) != 1) {
            free(palData); free(block); fclose(fp);
            return 0;
        }
    }

    /* Read raw image pixels */
    unsigned int rawSize = (unsigned int)hdr.width * hdr.height *
                           ((hdr.pixelDepth + 7) / 8);
    assert(rawSize > 0);
    unsigned char *rawBits = (unsigned char *)malloc(rawSize);
    assert(rawBits != NULL);
    if (!rawBits) {
        free(palData); free(block); fclose(fp);
        return 0;
    }

    if (fread(rawBits, rawSize, 1, fp) != 1) {
        free(rawBits); free(palData); free(block); fclose(fp);
        return 0;
    }

    fclose(fp);

    /* Attach to block — dfPtr (deferred pointer) is the palette buffer */
    block->dfPtr   = (unsigned int)(uintptr_t)palData;  /* may be 0 */
    block->rawBits = rawBits;
    block->rawSize = rawSize;
    assert(block->dfPtr != 0 || hdr.colorMapType == 0);

    *outBlock = block;
    return 1;
}


/* ─────────────────────────────────────────────────────────────────
 *  CreateImageColorLayerFromTga()
 *  Converts TGA pixel data to a flat RGBA8 colour layer.
 *  Handles: indexed (colour-mapped), true-colour RGB, intensity/greyscale.
 *  VA: ~0x416a30 (63 basic blocks, 838 bytes)
 *
 *  Error strings:
 *    "CreateImageColorLayerFromTga(): unknown pixel depth for file %s intensity data"
 *    "CreateImageColorLayerFromTga(): unknown pixel depth for file %s data"
 *    "CreateImageColorLayerFromTga(): unknown pixel depth for file %s colormapped data"
 *    "CreateImageColorLayerFromTga(): couldn't allocate layer buffer"
 * ───────────────────────────────────────────────────────────────── */

static unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block,
                                                    const char    *filename)
{
    unsigned int numPixels = (unsigned int)block->width * block->height;
    unsigned char *layer = (unsigned char *)malloc(numPixels * 4);
    if (!layer) {
        fprintf(stderr, "CreateImageColorLayerFromTga(): couldn't allocate layer buffer\n");
        return NULL;
    }

    unsigned char *src = block->rawBits;
    unsigned char *dst = layer;

    switch (block->imageType) {

        /* ── Greyscale / intensity ──────────────────────────── */
        case TGA_TYPE_GRAYSCALE:
        case TGA_TYPE_RLE_GRAYSCALE: {
            if (block->pixelDepth == 8) {
                for (unsigned int i = 0; i < numPixels; i++, src++) {
                    *dst++ = *src;  /* R */
                    *dst++ = *src;  /* G */
                    *dst++ = *src;  /* B */
                    *dst++ = 0xFF;  /* A */
                }
            } else if (block->pixelDepth == 16) {
                /* IA8: byte0=intensity, byte1=alpha */
                for (unsigned int i = 0; i < numPixels; i++, src += 2) {
                    *dst++ = src[0];  /* R = I */
                    *dst++ = src[0];  /* G = I */
                    *dst++ = src[0];  /* B = I */
                    *dst++ = src[1];  /* A */
                }
            } else {
                fprintf(stderr,
                    "CreateImageColorLayerFromTga(): unknown pixel depth"
                    " for file %s intensity data\n", filename);
                free(layer);
                return NULL;
            }
            break;
        }

        /* ── True-colour RGB ────────────────────────────────── */
        case TGA_TYPE_TRUE_COLOR:
        case TGA_TYPE_RLE_TRUE_COLOR: {
            if (block->pixelDepth == 16) {
                /* RGB555: B[4:0] G[9:5] R[14:10] X[15] */
                for (unsigned int i = 0; i < numPixels; i++, src += 2) {
                    unsigned short px = (unsigned short)(src[0] | (src[1] << 8));
                    *dst++ = (unsigned char)(((px >> 10) & 0x1F) << 3);  /* R */
                    *dst++ = (unsigned char)(((px >>  5) & 0x1F) << 3);  /* G */
                    *dst++ = (unsigned char)(( px        & 0x1F) << 3);  /* B */
                    *dst++ = 0xFF;
                }
            } else if (block->pixelDepth == 24) {
                for (unsigned int i = 0; i < numPixels; i++, src += 3) {
                    *dst++ = src[2];  /* R */
                    *dst++ = src[1];  /* G */
                    *dst++ = src[0];  /* B */
                    *dst++ = 0xFF;
                }
            } else if (block->pixelDepth == 32) {
                for (unsigned int i = 0; i < numPixels; i++, src += 4) {
                    *dst++ = src[2];  /* R */
                    *dst++ = src[1];  /* G */
                    *dst++ = src[0];  /* B */
                    *dst++ = src[3];  /* A */
                }
            } else {
                fprintf(stderr,
                    "CreateImageColorLayerFromTga(): unknown pixel depth"
                    " for file %s data\n", filename);
                free(layer);
                return NULL;
            }
            break;
        }

        /* ── Colour-indexed (palette) ──────────────────────── */
        case TGA_TYPE_COLOR_MAPPED:
        case TGA_TYPE_RLE_COLOR_MAPPED: {
            unsigned char *pal = (unsigned char *)(uintptr_t)block->dfPtr;
            unsigned int   entryBytes = (block->colorMapDepth + 7) / 8;

            if (block->pixelDepth == 8) {
                for (unsigned int i = 0; i < numPixels; i++, src++) {
                    unsigned char *pe = pal + (*src) * entryBytes;
                    if (entryBytes == 3) {
                        *dst++ = pe[2]; *dst++ = pe[1]; *dst++ = pe[0];
                        *dst++ = 0xFF;
                    } else if (entryBytes == 4) {
                        *dst++ = pe[2]; *dst++ = pe[1]; *dst++ = pe[0];
                        *dst++ = pe[3];
                    } else {
                        /* 15/16-bit palette entry (RGB555 or ARGB1555) */
                        unsigned short px = (unsigned short)(pe[0] | (pe[1] << 8));
                        *dst++ = (unsigned char)(((px >> 10) & 0x1F) << 3);
                        *dst++ = (unsigned char)(((px >>  5) & 0x1F) << 3);
                        *dst++ = (unsigned char)(( px        & 0x1F) << 3);
                        *dst++ = (block->colorMapDepth == 16 && (px & 0x8000)) ? 0 : 0xFF;
                    }
                }
            } else {
                fprintf(stderr,
                    "CreateImageColorLayerFromTga(): unknown pixel depth"
                    " for file %s colormapped data\n", filename);
                free(layer);
                return NULL;
            }
            break;
        }

        default:
            fprintf(stderr,
                "CreateImageColorLayerFromTga(): unknown pixel depth"
                " for file %s data\n", filename);
            free(layer);
            return NULL;
    }

    block->colorLayer = layer;
    return layer;
}


/* ─────────────────────────────────────────────────────────────────
 *  CreateImageAlphaLayerFromTga()
 *  Extracts a separate 8-bit alpha channel from a TGA image.
 *  VA: ~0x416e70 (20 blocks, 292 bytes)
 *
 *  Error string:
 *    "CreateImageAlphaLayerFromTga(): intensity file %s has unknown pixel depth"
 * ───────────────────────────────────────────────────────────────── */

static unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block,
                                                    const char    *filename)
{
    unsigned int  numPixels = (unsigned int)block->width * block->height;
    unsigned char *layer    = (unsigned char *)malloc(numPixels);
    if (!layer) return NULL;

    unsigned char *src = block->rawBits;
    unsigned char *dst = layer;

    if (block->imageType == TGA_TYPE_GRAYSCALE ||
        block->imageType == TGA_TYPE_RLE_GRAYSCALE)
    {
        /* Intensity images: alpha comes from the 2nd byte (IA8) or is opaque */
        if (block->pixelDepth == 16) {
            /* IA8: byte[0]=intensity, byte[1]=alpha */
            for (unsigned int i = 0; i < numPixels; i++, src += 2)
                *dst++ = src[1];
        } else if (block->pixelDepth == 8) {
            /* 8-bit greyscale: treat as fully opaque */
            memset(dst, 0xFF, numPixels);
        } else {
            fprintf(stderr,
                "CreateImageAlphaLayerFromTga(): intensity file %s has unknown pixel depth\n",
                filename);
            free(layer);
            return NULL;
        }
    } else if (block->imageType == TGA_TYPE_TRUE_COLOR ||
               block->imageType == TGA_TYPE_RLE_TRUE_COLOR)
    {
        if (block->pixelDepth == 32) {
            /* BGRA: alpha is byte[3] */
            for (unsigned int i = 0; i < numPixels; i++, src += 4)
                *dst++ = src[3];
        } else {
            /* 16/24-bit: no alpha channel — fully opaque */
            memset(dst, 0xFF, numPixels);
        }
    } else {
        /* Colour-mapped: no separate alpha layer in this tool */
        memset(dst, 0xFF, numPixels);
    }

    block->alphaLayer = layer;
    return layer;
}


/* ─────────────────────────────────────────────────────────────────
 *  AdjustTgaForPaletteOffset()
 *  Re-bases colour-mapped pixel indices when the palette has a non-zero
 *  colorMapOrigin, so that index 0 == first used entry after the offset.
 *  VA: ~0x416350 (65 blocks, 1004 bytes — large function)
 *
 *  Error string:
 *    "AdjustTgaForPaletteOffset(): unsupported pixel depth"
 * ───────────────────────────────────────────────────────────────── */

static void AdjustTgaForPaletteOffset(TgaImageBlock *block)
{
    if (block->paletteOffset == 0)
        return;  /* nothing to do */

    unsigned int numPixels = (unsigned int)block->width * block->height;

    if (block->pixelDepth == 8) {
        unsigned char offset = block->paletteOffset;
        unsigned char *px    = block->rawBits;
        for (unsigned int i = 0; i < numPixels; i++, px++)
            *px = (unsigned char)(*px + offset);
    } else {
        fprintf(stderr, "AdjustTgaForPaletteOffset(): unsupported pixel depth\n");
    }
}


/* ─────────────────────────────────────────────────────────────────
 *  TCWriteTplFile()
 *  Writes the final PCP (Picture Pack) file in GameCube TPL format.
 *  For each image it:
 *    1. Validates wrap modes vs. power-of-two dimensions (GX hardware rule).
 *    2. Converts the RGBA8 colour+alpha layers to the target GXTexFmt.
 *    3. Writes palette blocks (WritePaletteBlockRGB565/RGB5A3) for CI types.
 *    4. Serialises the TPL header + image/palette descriptors + pixel data.
 *  VA: ~0x41c4d0 + related functions (0x41be30, 0x4169a0, 0x41a3f0, …)
 *
 *  Key error strings:
 *    "TCWriteTplFile: since width %d is not a power of two, wrapS mode must
 *     be GX_CLAMP for image %d in script file"
 *    "TCWriteTplFile: since height %d is not a power of two, wrapT mode must
 *     be GX_CLAMP for image %d in script file"
 *    "TCSetTplPaletteValues: unknown entry format for palette %d"
 *    "TCSetTplPaletteValues: num entries ... for palette %d"
 * ───────────────────────────────────────────────────────────────── */

static int TCWriteTplFile(TplState *state, const char *outPcpPath)
{
    /* First pass: load, decode, and convert all TGA sources */
    for (int i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
        if (img->tgaPath[0] == '\0') continue;  /* NULL slot */

        TgaImageBlock *block = NULL;
        if (!DecodeTgaFile(img->tgaPath, &block)) {
            fprintf(stderr, "Error: DecodeTgaFile failed for '%s'\n", img->tgaPath);
            return -1;
        }

        img->width  = block->width;
        img->height = block->height;

        /* GameCube GX hardware constraint: non-power-of-two dimensions
         * require GX_CLAMP wrap mode (see TCWriteTplFile warning strings). */
        if (!IsPowerOfTwo(img->width) && img->wrapS != GX_CLAMP) {
            fprintf(stderr,
                "TCWriteTplFile: since width %d is not a power of two,"
                " wrapS mode must be GX_CLAMP for image %d in script file\n",
                img->width, i);
            img->wrapS = GX_CLAMP;
        }
        if (!IsPowerOfTwo(img->height) && img->wrapT != GX_CLAMP) {
            fprintf(stderr,
                "TCWriteTplFile: since height %d is not a power of two,"
                " wrapT mode must be GX_CLAMP for image %d in script file\n",
                img->height, i);
            img->wrapT = GX_CLAMP;
        }

        /* Create colour + alpha layers */
        img->colorData = CreateImageColorLayerFromTga(block, img->tgaPath);
        img->alphaData = CreateImageAlphaLayerFromTga(block, img->tgaPath);

        if (img->colorData == NULL || img->alphaData == NULL) {
            free(block->rawBits);
            free((void *)(uintptr_t)block->dfPtr);
            free(block);
            return -1;
        }

        /* For palette-based formats, adjust palette origin */
        if (block->colorMapType == 1)
            AdjustTgaForPaletteOffset(block);

        free(block->rawBits);
        free((void *)(uintptr_t)block->dfPtr);
        free(block);
    }

    /* Second pass: load and convert palette TGA sources */
    for (int p = 0; p < state->numPalettes; p++) {
        if (TCSetTplPaletteValues(state, p) != 0) {
            fprintf(stderr, "TCWriteTplFile: failed to set palette %d\n", p);
            return -1;
        }
    }

    /* Write the PCP/TPL binary file */
    FILE *fp = fopen(outPcpPath, "wb");
    if (!fp) {
        fprintf(stderr, "TCWriteTplFile: couldn't open %s for write\n", outPcpPath);
        return -1;
    }

    /* ── PCP outer header (Capcom "PIC" magic) ──────────────────
     * The PICReader class (VA string 0x44f0d0) validates:
     *   "PICファイルのmagicが違います。"  (wrong magic value)
     * Magic = 0x50494300 ("PIC\0"), big-endian.
     */
    unsigned int magic = PCP_MAGIC;
    fwrite(&magic, 4, 1, fp);

    /* Image count (big-endian u32) */
    unsigned int imgCount = (unsigned int)state->numImages;
    /* swap to big-endian */
    unsigned char cnt_be[4] = {
        (imgCount >> 24) & 0xFF,
        (imgCount >> 16) & 0xFF,
        (imgCount >>  8) & 0xFF,
        (imgCount >>  0) & 0xFF
    };
    fwrite(cnt_be, 4, 1, fp);

    /* ── Per-image descriptors ─────────────────────────────────── */
    for (int i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];

        /* Write per-image entry: format, wrap, dimensions
         * (Exact descriptor layout follows the GameCube TPL spec) */
        unsigned char desc[16] = {0};
        desc[0]  = (unsigned char)img->texFmt;
        desc[1]  = (unsigned char)img->wrapS;
        desc[2]  = (unsigned char)img->wrapT;
        desc[3]  = (unsigned char)img->numMips;
        desc[4]  = (img->width  >> 8) & 0xFF;
        desc[5]  = (img->width       ) & 0xFF;
        desc[6]  = (img->height >> 8) & 0xFF;
        desc[7]  = (img->height      ) & 0xFF;
        desc[8]  = (unsigned char)img->paletteIndex;
        fwrite(desc, 16, 1, fp);
    }

    /* ── Palette blocks ────────────────────────────────────────── */
    for (int p = 0; p < state->numPalettes; p++) {
        TplPaletteEntry *pal = &state->palettes[p];
        if (pal->palFmt == GX_CLR_RGB565) {
            WritePaletteBlockRGB565(fp, pal, p);
        } else if (pal->palFmt == GX_CLR_RGB5A3) {
            WritePaletteBlockRGB5A3(fp, pal, p);
        } else {
            fprintf(stderr,
                "TCSetTplPaletteValues: unknown entry format for palette %d\n", p);
        }
    }

    /* ── Pixel data blocks ─────────────────────────────────────── */
    for (int i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
        if (!img->colorData) continue;

        unsigned int numPx = (unsigned int)img->width * img->height;
        /* In this simplified reconstruction we write RGBA8.
         * The real tool would convert to the target GXTexFmt using
         * GX-format encoding functions (IA4, RGB565, RGB5A3, CI8, CMPR …) */
        fwrite(img->colorData, numPx * 4, 1, fp);
    }

    fclose(fp);
    return 0;
}


/* ─────────────────────────────────────────────────────────────────
 *  TCSetTplPaletteValues()
 *  Decodes the palette TGA and converts to the target GXTlutFmt.
 *  Error strings:
 *    "TCSetTplPaletteValues: unknown entry format for palette %d"
 *    "TCSetTplPaletteValues: num entries ... for palette %d"
 * ───────────────────────────────────────────────────────────────── */

static int TCSetTplPaletteValues(TplState *state, int paletteIdx)
{
    TplPaletteEntry *pal = &state->palettes[paletteIdx];
    if (pal->tgaPath[0] == '\0') return 0;  /* no palette for this slot */

    TgaImageBlock *block = NULL;
    if (!DecodeTgaFile(pal->tgaPath, &block)) return -1;

    unsigned int numColors = (unsigned int)block->width * block->height;
    pal->numColors = (int)numColors;

    unsigned char *rgba = CreateImageColorLayerFromTga(block, pal->tgaPath);
    if (!rgba) {
        free(block->rawBits);
        free(block);
        return -1;
    }

    unsigned char *palOut = (unsigned char *)malloc(numColors * 2);  /* 2 bytes/entry */
    if (!palOut) {
        free(rgba);
        free(block->rawBits);
        free(block);
        return -1;
    }

    unsigned char *src = rgba;
    unsigned short *dst = (unsigned short *)palOut;

    switch (pal->palFmt) {
        case GX_CLR_RGB565:
            for (unsigned int i = 0; i < numColors; i++, src += 4) {
                /* RGBA → RGB565 big-endian */
                unsigned short v = (unsigned short)(
                    (((unsigned)src[0] >> 3) << 11) |
                    (((unsigned)src[1] >> 2) <<  5) |
                    (((unsigned)src[2] >> 3)       ));
                *dst++ = (unsigned short)((v >> 8) | (v << 8));  /* byte-swap */
            }
            break;

        case GX_CLR_RGB5A3:
            for (unsigned int i = 0; i < numColors; i++, src += 4) {
                unsigned short v;
                if (src[3] >= 0xE8) {
                    /* Opaque: 1 RRRRR GGGGG BBBBB */
                    v = (unsigned short)(0x8000 |
                        (((unsigned)src[0] >> 3) << 10) |
                        (((unsigned)src[1] >> 3) <<  5) |
                        (((unsigned)src[2] >> 3)       ));
                } else {
                    /* Translucent: 0 AAA RRRR GGGG BBBB */
                    v = (unsigned short)(
                        (((unsigned)src[3] >> 5) << 12) |
                        (((unsigned)src[0] >> 4) <<  8) |
                        (((unsigned)src[1] >> 4) <<  4) |
                        (((unsigned)src[2] >> 4)       ));
                }
                *dst++ = (unsigned short)((v >> 8) | (v << 8));
            }
            break;

        default:
            fprintf(stderr,
                "TCSetTplPaletteValues: unknown entry format for palette %d\n",
                paletteIdx);
            free(palOut);
            free(rgba);
            free(block->rawBits);
            free(block);
            return -1;
    }

    pal->palData = palOut;
    free(rgba);
    free(block->rawBits);
    free((void *)(uintptr_t)block->dfPtr);
    free(block);
    return 0;
}


/* ─────────────────────────────────────────────────────────────────
 *  TCGetTplVersion()
 *  Reads the version word from an existing TPL file header.
 *  Error string:
 *    "TCGetTplVersion: couldn't open existing tpl %s for read"
 * ───────────────────────────────────────────────────────────────── */

static int TCGetTplVersion(const char *existingTplPath)
{
    FILE *fp = fopen(existingTplPath, "rb");
    if (!fp) {
        fprintf(stderr, "TCGetTplVersion: couldn't open existing tpl %s for read\n",
                existingTplPath);
        return -1;
    }
    unsigned int magic;
    fread(&magic, 4, 1, fp);
    int version = -1;
    if (magic == PCP_MAGIC) {
        unsigned int ver;
        if (fread(&ver, 4, 1, fp) == 1)
            version = (int)ver;
    }
    fclose(fp);
    return version;
}


/* ─────────────────────────────────────────────────────────────────
 *  WritePaletteBlockRGB565() / WritePaletteBlockRGB5A3()
 *  Write a palette colour table in the given GXTlutFmt to the output file.
 *  Error strings:
 *    "WritePaletteBlockRGB565:  error in fwrite for palette %d"
 *    "WritePaletteBlockRGB5A3:  error in fwrite for palette %d"
 * ───────────────────────────────────────────────────────────────── */

static int WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx)
{
    /* Header: palette format word (big-endian) + entry count */
    unsigned short fmtBE   = 0x0001;  /* GX_TL_RGB565 */
    unsigned int   cntBE   = (unsigned int)pal->numColors;
    fwrite(&fmtBE, 2, 1, fp);
    fwrite(&cntBE, 4, 1, fp);

    size_t written = fwrite(pal->palData, 2, (size_t)pal->numColors, fp);
    if ((int)written != pal->numColors) {
        fprintf(stderr,
            "WritePaletteBlockRGB565:  error in fwrite for palette %d\n", paletteIdx);
        return -1;
    }
    return 0;
}

static int WritePaletteBlockRGB5A3(FILE *fp, TplPaletteEntry *pal, int paletteIdx)
{
    unsigned short fmtBE = 0x0002;  /* GX_TL_RGB5A3 */
    unsigned int   cntBE = (unsigned int)pal->numColors;
    fwrite(&fmtBE, 2, 1, fp);
    fwrite(&cntBE, 4, 1, fp);

    size_t written = fwrite(pal->palData, 2, (size_t)pal->numColors, fp);
    if ((int)written != pal->numColors) {
        fprintf(stderr,
            "WritePaletteBlockRGB5A3:  error in fwrite for palette %d\n", paletteIdx);
        return -1;
    }
    return 0;
}


/* ─────────────────────────────────────────────────────────────────
 *  Helper: string → enum parsers
 *  (Values driven by global string tables at VA 0x4547bc / 0x454834)
 * ───────────────────────────────────────────────────────────────── */

static GXTexFmt ParseTexFmt(const char *s)
{
    if (!s) return GX_TF_INVALID;
    if (strcmp(s, "IA4"    ) == 0) return GX_TF_IA4;
    if (strcmp(s, "IA8"    ) == 0) return GX_TF_IA8;
    if (strcmp(s, "RGB565" ) == 0) return GX_TF_RGB565;
    if (strcmp(s, "RGB5A3" ) == 0) return GX_TF_RGB5A3;
    if (strcmp(s, "RGBA8"  ) == 0) return GX_TF_RGBA8;
    if (strcmp(s, "CI4"    ) == 0) return GX_TF_CI4;
    if (strcmp(s, "CI8"    ) == 0) return GX_TF_CI8;
    if (strcmp(s, "CI14_X2") == 0) return GX_TF_CI14X2;
    if (strcmp(s, "CMPR"   ) == 0) return GX_TF_CMPR;
    return GX_TF_INVALID;
}

static GXTlutFmt ParseTlutFmt(const char *s)
{
    if (!s) return GX_CLR_INVALID;
    if (strcmp(s, "RGB565") == 0) return GX_CLR_RGB565;
    if (strcmp(s, "RGB5A3") == 0) return GX_CLR_RGB5A3;
    if (strcmp(s, "IA8"   ) == 0) return GX_CLR_IA8;
    return GX_CLR_INVALID;
}

static GXTexWrapMode ParseWrapMode(const char *s)
{
    if (!s) return GX_WRAP_INVALID;
    if (strcmp(s, "GX_CLAMP" ) == 0) return GX_CLAMP;
    if (strcmp(s, "GX_REPEAT") == 0) return GX_REPEAT;
    if (strcmp(s, "GX_MIRROR") == 0) return GX_MIRROR;
    return GX_WRAP_INVALID;
}

static int IsPowerOfTwo(unsigned int x)
{
    return (x > 0) && ((x & (x - 1)) == 0);
}
