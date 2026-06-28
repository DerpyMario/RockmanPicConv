name=RockmanPicConv.c
/* RockmanPicConv - adapted for MSVC6
 *
 * This file was reconstructed from decompiled C and adapted for
 * Microsoft Visual C++ 6.0:
 *  - No C99 loop-scope declarations
 *  - Uses _snprintf for MSVC6
 *  - TgaImageBlock.dfPtr is an unsigned char* to avoid uintptr_t
 *
 * Build:
 *  - Create a Win32 Console Project in Visual C++ 6.0
 *  - Add this file to the project
 *  - Build as a console application
 *
 * Note: This is the reconstructed logic only. It writes RGBA8 raw
 * pixel blocks to the output PCP/TPL; full GX encoding/CMPR/etc.
 * would require more code (not included).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

/* ─────────────────────────────────────────────────────────────────
 *  GameCube GX texture / wrap-mode enumerations
 * ───────────────────────────────────────────────────────────────── */

typedef enum {
    GX_TF_IA4     = 0,
    GX_TF_IA8     = 1,
    GX_TF_RGB565  = 2,
    GX_TF_RGB5A3  = 3,
    GX_TF_RGBA8   = 4,
    GX_TF_CI4     = 5,
    GX_TF_CI8     = 6,
    GX_TF_CI14X2  = 7,
    GX_TF_CMPR    = 8,
    GX_TF_INVALID = -1
} GXTexFmt;

typedef enum {
    GX_CLR_RGB565 = 0,
    GX_CLR_RGB5A3 = 1,
    GX_CLR_IA8    = 2,
    GX_CLR_INVALID = -1
} GXTlutFmt;

typedef enum {
    GX_CLAMP  = 0,
    GX_REPEAT = 1,
    GX_MIRROR = 2,
    GX_WRAP_INVALID = -1
} GXTexWrapMode;

static const char *g_texFmtNames[] = {
    "IA4", "IA8", "RGB565", "RGB5A3", "RGBA8", "CI4", "CI8", "CI14_X2", "CMPR", NULL
};

static const char *g_wrapModeNames[] = {
    "GX_CLAMP", "GX_REPEAT", "GX_MIRROR", NULL
};

/* ─────────────────────────────────────────────────────────────────
 *  TGA file-format structures
 * ───────────────────────────────────────────────────────────────── */

#pragma pack(push, 1)
typedef struct {
    unsigned char  idLength;
    unsigned char  colorMapType;
    unsigned char  imageType;
    unsigned short colorMapOrigin;
    unsigned short colorMapLength;
    unsigned char  colorMapDepth;
    unsigned short xOrigin;
    unsigned short yOrigin;
    unsigned short width;
    unsigned short height;
    unsigned char  pixelDepth;
    unsigned char  imageDescriptor;
} TgaHeader;
#pragma pack(pop)

#define TGA_TYPE_NO_IMAGE          0
#define TGA_TYPE_COLOR_MAPPED      1
#define TGA_TYPE_TRUE_COLOR        2
#define TGA_TYPE_GRAYSCALE         3
#define TGA_TYPE_RLE_COLOR_MAPPED  9
#define TGA_TYPE_RLE_TRUE_COLOR   10
#define TGA_TYPE_RLE_GRAYSCALE    11

typedef struct TgaImageBlock {
    unsigned char  *rawBits;
    unsigned int    rawSize;
    unsigned char  *dfPtr;          /* changed to pointer for MSVC6 */
    unsigned int    reserved0;
    unsigned int    reserved1;
    unsigned int    imageType;
    unsigned short  width;
    unsigned short  height;
    unsigned char   pixelDepth;
    unsigned char   colorMapDepth;
    unsigned char   imageDescriptor;
    unsigned char   paletteOffset;
    unsigned char  *colorLayer;
    unsigned char  *alphaLayer;
} TgaImageBlock;

/* PCP / TPL structures */
#define PCP_MAGIC  0x50494300u

#define SCRIPT_KW_PATH    "PATH"
#define SCRIPT_KW_NULL    "NULL"
#define SCRIPT_KW_FILE    "FILE"
#define SCRIPT_KW_IMAGE   "IMAGE"
#define SCRIPT_KW_PALETTE "PALETTE"
#define SCRIPT_KW_TEXTURE "TEXTURE"

typedef struct TplImageEntry {
    char           tgaPath[512];
    GXTexFmt       texFmt;
    GXTlutFmt      palFmt;
    GXTexWrapMode  wrapS;
    GXTexWrapMode  wrapT;
    int            paletteIndex;
    unsigned short width;
    unsigned short height;
    unsigned char *colorData;
    unsigned char *alphaData;
    int            numMips;
} TplImageEntry;

typedef struct TplPaletteEntry {
    char           tgaPath[512];
    GXTlutFmt      palFmt;
    int            numColors;
    unsigned char *palData;
} TplPaletteEntry;

typedef struct TplState {
    char              basePath[512];
    int               numImages;
    int               numPalettes;
    TplImageEntry    *images;
    TplPaletteEntry  *palettes;
    int               version;
} TplState;

/* Forward declarations */
static int  DecodeTgaFile(const char *filename, TgaImageBlock **outBlock);
static unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block, const char *filename);
static unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block, const char *filename);
static void AdjustTgaForPaletteOffset(TgaImageBlock *block);

static int  TCReadTplTextFile(TplState *state, const char *scriptPath);
static int  TCWriteTplFile(TplState *state, const char *outPcpPath);
static int  TCSetTplPaletteValues(TplState *state, int paletteIdx);
static int  TCGetTplVersion(const char *existingTplPath);
static int  WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx);
static int  WritePaletteBlockRGB5A3(FILE *fp, TplPaletteEntry *pal, int paletteIdx);

static GXTexFmt      ParseTexFmt(const char *s);
static GXTlutFmt     ParseTlutFmt(const char *s);
static GXTexWrapMode ParseWrapMode(const char *s);
static int           IsPowerOfTwo(unsigned int x);

/* main */
int main(int argc, char **argv)
{
    printf("=============================================\n");
    printf("RockmanEXE Cube Picture Converter \"RockmanPicConv\"\n");
    printf("=============================================\n");

    if (argc < 3) {
        printf("usage : RockmanPicConv <script file> <out pcp file>\n");
        printf("  <script file>  -- TPL script file\n");
        printf("  <out pcp file> -- output Picture Pack file\n");
        return 1;
    }

    const char *scriptPath  = argv[1];
    const char *outPcpPath  = argv[2];

    TplState state;
    memset(&state, 0, sizeof(state));

    if (TCReadTplTextFile(&state, scriptPath) != 0) {
        fprintf(stderr, "Error: failed to read script file '%s'\n", scriptPath);
        return 1;
    }

    int totalBytes = 0;
    {
        int i;
        for (i = 0; i < state.numImages; i++) {
            totalBytes += state.images[i].width * state.images[i].height * 4;
        }
    }
    printf("O = %d\n", state.numImages);
    printf("Y : %d bytes\n", (totalBytes + 0x3FF) >> 10);

    if (TCWriteTplFile(&state, outPcpPath) != 0) {
        fprintf(stderr, "Error: failed to write PCP file '%s'\n", outPcpPath);
    }

    if (state.images) {
        int i;
        for (i = 0; i < state.numImages; i++) {
            free(state.images[i].colorData);
            free(state.images[i].alphaData);
        }
        free(state.images);
    }
    if (state.palettes) {
        int p;
        for (p = 0; p < state.numPalettes; p++) {
            free(state.palettes[p].palData);
        }
        free(state.palettes);
    }

    return 0;
}

/* TCReadTplTextFile */
#define SCRIPT_MAX_LINE  1024
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

        if ((int)strlen(line) > SCRIPT_MAX_LINE) {
            fprintf(stderr, "TCReadTplTextFile: line exceeded %d characters in '%s'\n",
                    SCRIPT_MAX_LINE, scriptPath);
            fclose(fp);
            return -1;
        }

        char *nl = strpbrk(line, "\r\n");
        if (nl) *nl = '\0';

        if (line[0] == '\0' || line[0] == '#' || line[0] == ';')
            continue;

        char keyword[64];
        if (sscanf(line, "%63s", keyword) != 1)
            continue;

        if (strcmp(keyword, SCRIPT_KW_PATH) == 0) {
            _snprintf(state->basePath, sizeof(state->basePath), "%s", "");
            sscanf(line, "%*s %511s", state->basePath);
            continue;
        }

        if (strcmp(keyword, SCRIPT_KW_NULL) == 0) {
            if (imageIdx >= 0 && imageIdx < state->numImages)
                imageIdx++;
            continue;
        }

        if (strcmp(keyword, SCRIPT_KW_FILE) == 0) {
            int n;
            if (sscanf(line, "%*s %d", &n) == 1 && n > 0) {
                state->numImages = n;
                state->images    = (TplImageEntry *)calloc(n, sizeof(TplImageEntry));
                if (!state->images) { fclose(fp); return -1; }
                allocImages = n;
            }
            continue;
        }

        if (strcmp(keyword, SCRIPT_KW_IMAGE) == 0) {
            int idx;
            if (sscanf(line, "%*s %d", &idx) == 1)
                imageIdx = idx;
            continue;
        }

        if (strcmp(keyword, SCRIPT_KW_PALETTE) == 0) {
            int idx;
            if (sscanf(line, "%*s %d", &idx) == 1) {
                paletteIdx = idx;

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

                char palFile[256], fmtStr[64];
                if (sscanf(line, "%*s %*d %255s %63s", palFile, fmtStr) == 2) {
                    _snprintf(state->palettes[idx].tgaPath,
                             sizeof(state->palettes[idx].tgaPath),
                             "%s/%s", state->basePath, palFile);
                    state->palettes[idx].palFmt = ParseTlutFmt(fmtStr);
                }
            }
            continue;
        }

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
                _snprintf(img->tgaPath, sizeof(img->tgaPath), "%s/%s", state->basePath, texFile);
                img->texFmt = ParseTexFmt(fmtStr);
                img->wrapS  = ParseWrapMode(wrapSStr);
                img->wrapT  = ParseWrapMode(wrapTStr);
                img->paletteIndex = paletteIdx;
            }
            imageIdx++;
            continue;
        }
    }

    fclose(fp);
    return 0;
}

/* DecodeTgaFile */
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

    if (hdr.idLength > 0)
        fseek(fp, hdr.idLength, SEEK_CUR);

    if (hdr.imageType == TGA_TYPE_NO_IMAGE && hdr.colorMapType == 0) {
        fprintf(stderr, "DecodeTgaFile(): file %s contains no image or palette data\n",
                filename);
        fclose(fp);
        return 0;
    }

    if (hdr.colorMapType == 1) {
        if (hdr.colorMapDepth != 15 && hdr.colorMapDepth != 16 &&
            hdr.colorMapDepth != 24 && hdr.colorMapDepth != 32)
        {
            fprintf(stderr, "DecodeTgaFile(): unsupported palette bit depth in file %s\n",
                    filename);
            fclose(fp);
            return 0;
        }
        unsigned int entryBytes = (hdr.colorMapDepth + 7) / 8;
        if (entryBytes == 0 || entryBytes > 4) {
            fprintf(stderr, "DecodeTgaFile(): unsupported palette entry size in file %s\n",
                    filename);
            fclose(fp);
            return 0;
        }
    }

    TgaImageBlock *block = (TgaImageBlock *)calloc(1, sizeof(TgaImageBlock));
    if (!block) { fclose(fp); return 0; }

    block->imageType       = hdr.imageType;
    block->width           = hdr.width;
    block->height          = hdr.height;
    block->pixelDepth      = hdr.pixelDepth;
    block->colorMapDepth   = hdr.colorMapDepth;
    block->imageDescriptor = hdr.imageDescriptor;

    unsigned char *palData = NULL;
    if (hdr.colorMapType == 1 && hdr.colorMapLength > 0) {
        unsigned int entryBytes = (hdr.colorMapDepth + 7) / 8;
        unsigned int palBytes   = hdr.colorMapLength * entryBytes;
        palData = (unsigned char *)malloc(palBytes);
        if (!palData) { free(block); fclose(fp); return 0; }

        if (hdr.colorMapOrigin > 0)
            fseek(fp, hdr.colorMapOrigin * entryBytes, SEEK_CUR);

        if (fread(palData, palBytes, 1, fp) != 1) {
            free(palData); free(block); fclose(fp);
            return 0;
        }
    }

    unsigned int rawSize = (unsigned int)hdr.width * hdr.height * ((hdr.pixelDepth + 7) / 8);
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

    block->dfPtr   = palData;
    block->rawBits = rawBits;
    block->rawSize = rawSize;
    assert(block->dfPtr != NULL || hdr.colorMapType == 0);

    *outBlock = block;
    return 1;
}

/* CreateImageColorLayerFromTga */
static unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block, const char *filename)
{
    unsigned int numPixels = (unsigned int)block->width * block->height;
    unsigned char *layer = (unsigned char *)malloc(numPixels * 4);
    if (!layer) {
        fprintf(stderr, "CreateImageColorLayerFromTga(): couldn't allocate layer buffer\n");
        return NULL;
    }

    unsigned char *src = block->rawBits;
    unsigned char *dst = layer;
    {
        unsigned int i;
        switch (block->imageType) {
            case TGA_TYPE_GRAYSCALE:
            case TGA_TYPE_RLE_GRAYSCALE:
                if (block->pixelDepth == 8) {
                    for (i = 0; i < numPixels; i++, src++) {
                        *dst++ = *src;
                        *dst++ = *src;
                        *dst++ = *src;
                        *dst++ = 0xFF;
                    }
                } else if (block->pixelDepth == 16) {
                    for (i = 0; i < numPixels; i++, src += 2) {
                        *dst++ = src[0];
                        *dst++ = src[0];
                        *dst++ = src[0];
                        *dst++ = src[1];
                    }
                } else {
                    fprintf(stderr,
                        "CreateImageColorLayerFromTga(): unknown pixel depth for file %s intensity data\n", filename);
                    free(layer);
                    return NULL;
                }
                break;

            case TGA_TYPE_TRUE_COLOR:
            case TGA_TYPE_RLE_TRUE_COLOR:
                if (block->pixelDepth == 16) {
                    for (i = 0; i < numPixels; i++, src += 2) {
                        unsigned short px = (unsigned short)(src[0] | (src[1] << 8));
                        *dst++ = (unsigned char)(((px >> 10) & 0x1F) << 3);
                        *dst++ = (unsigned char)(((px >>  5) & 0x1F) << 3);
                        *dst++ = (unsigned char)(( px        & 0x1F) << 3);
                        *dst++ = 0xFF;
                    }
                } else if (block->pixelDepth == 24) {
                    for (i = 0; i < numPixels; i++, src += 3) {
                        *dst++ = src[2];
                        *dst++ = src[1];
                        *dst++ = src[0];
                        *dst++ = 0xFF;
                    }
                } else if (block->pixelDepth == 32) {
                    for (i = 0; i < numPixels; i++, src += 4) {
                        *dst++ = src[2];
                        *dst++ = src[1];
                        *dst++ = src[0];
                        *dst++ = src[3];
                    }
                } else {
                    fprintf(stderr,
                        "CreateImageColorLayerFromTga(): unknown pixel depth for file %s data\n", filename);
                    free(layer);
                    return NULL;
                }
                break;

            case TGA_TYPE_COLOR_MAPPED:
            case TGA_TYPE_RLE_COLOR_MAPPED: {
                unsigned char *pal = block->dfPtr;
                unsigned int   entryBytes = (block->colorMapDepth + 7) / 8;
                if (block->pixelDepth == 8) {
                    for (i = 0; i < numPixels; i++, src++) {
                        unsigned char *pe = pal + (*src) * entryBytes;
                        if (entryBytes == 3) {
                            *dst++ = pe[2]; *dst++ = pe[1]; *dst++ = pe[0];
                            *dst++ = 0xFF;
                        } else if (entryBytes == 4) {
                            *dst++ = pe[2]; *dst++ = pe[1]; *dst++ = pe[0];
                            *dst++ = pe[3];
                        } else {
                            unsigned short px = (unsigned short)(pe[0] | (pe[1] << 8));
                            *dst++ = (unsigned char)(((px >> 10) & 0x1F) << 3);
                            *dst++ = (unsigned char)(((px >>  5) & 0x1F) << 3);
                            *dst++ = (unsigned char)(( px        & 0x1F) << 3);
                            *dst++ = (block->colorMapDepth == 16 && (px & 0x8000)) ? 0 : 0xFF;
                        }
                    }
                } else {
                    fprintf(stderr,
                        "CreateImageColorLayerFromTga(): unknown pixel depth for file %s colormapped data\n", filename);
                    free(layer);
                    return NULL;
                }
                break;
            }

            default:
                fprintf(stderr,
                    "CreateImageColorLayerFromTga(): unknown pixel depth for file %s data\n", filename);
                free(layer);
                return NULL;
        }
    }

    block->colorLayer = layer;
    return layer;
}

/* CreateImageAlphaLayerFromTga */
static unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block, const char *filename)
{
    unsigned int  numPixels = (unsigned int)block->width * block->height;
    unsigned char *layer    = (unsigned char *)malloc(numPixels);
    if (!layer) return NULL;

    unsigned char *src = block->rawBits;
    unsigned char *dst = layer;
    {
        unsigned int i;
        if (block->imageType == TGA_TYPE_GRAYSCALE ||
            block->imageType == TGA_TYPE_RLE_GRAYSCALE)
        {
            if (block->pixelDepth == 16) {
                for (i = 0; i < numPixels; i++, src += 2)
                    *dst++ = src[1];
            } else if (block->pixelDepth == 8) {
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
                for (i = 0; i < numPixels; i++, src += 4)
                    *dst++ = src[3];
            } else {
                memset(dst, 0xFF, numPixels);
            }
        } else {
            memset(dst, 0xFF, numPixels);
        }
    }

    block->alphaLayer = layer;
    return layer;
}

/* AdjustTgaForPaletteOffset */
static void AdjustTgaForPaletteOffset(TgaImageBlock *block)
{
    if (block->paletteOffset == 0)
        return;

    unsigned int numPixels = (unsigned int)block->width * block->height;
    if (block->pixelDepth == 8) {
        unsigned char offset = block->paletteOffset;
        unsigned char *px = block->rawBits;
        unsigned int i;
        for (i = 0; i < numPixels; i++, px++)
            *px = (unsigned char)(*px + offset);
    } else {
        fprintf(stderr, "AdjustTgaForPaletteOffset(): unsupported pixel depth\n");
    }
}

/* TCWriteTplFile */
static int TCWriteTplFile(TplState *state, const char *outPcpPath)
{
    int i;
    for (i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
        if (img->tgaPath[0] == '\0') continue;

        TgaImageBlock *block = NULL;
        if (!DecodeTgaFile(img->tgaPath, &block)) {
            fprintf(stderr, "Error: DecodeTgaFile failed for '%s'\n", img->tgaPath);
            return -1;
        }

        img->width  = block->width;
        img->height = block->height;

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

        img->colorData = CreateImageColorLayerFromTga(block, img->tgaPath);
        img->alphaData = CreateImageAlphaLayerFromTga(block, img->tgaPath);

        if (img->colorData == NULL || img->alphaData == NULL) {
            free(block->rawBits);
            free(block->dfPtr);
            free(block);
            return -1;
        }

        if (block->dfPtr)
            AdjustTgaForPaletteOffset(block);

        free(block->rawBits);
        free(block->dfPtr);
        free(block);
    }

    /* palettes */
    int p;
    for (p = 0; p < state->numPalettes; p++) {
        if (TCSetTplPaletteValues(state, p) != 0) {
            fprintf(stderr, "TCWriteTplFile: failed to set palette %d\n", p);
            return -1;
        }
    }

    FILE *fp = fopen(outPcpPath, "wb");
    if (!fp) {
        fprintf(stderr, "TCWriteTplFile: couldn't open %s for write\n", outPcpPath);
        return -1;
    }

    unsigned int magic = PCP_MAGIC;
    fwrite(&magic, 4, 1, fp);

    unsigned int imgCount = (unsigned int)state->numImages;
    unsigned char cnt_be[4] = {
        (imgCount >> 24) & 0xFF,
        (imgCount >> 16) & 0xFF,
        (imgCount >>  8) & 0xFF,
        (imgCount >>  0) & 0xFF
    };
    fwrite(cnt_be, 4, 1, fp);

    for (i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
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

    for (p = 0; p < state->numPalettes; p++) {
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

    for (i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
        if (!img->colorData) continue;
        unsigned int numPx = (unsigned int)img->width * img->height;
        fwrite(img->colorData, numPx * 4, 1, fp);
    }

    fclose(fp);
    return 0;
}

/* TCSetTplPaletteValues */
static int TCSetTplPaletteValues(TplState *state, int paletteIdx)
{
    TplPaletteEntry *pal = &state->palettes[paletteIdx];
    if (pal->tgaPath[0] == '\0') return 0;

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

    unsigned char *palOut = (unsigned char *)malloc(numColors * 2);
    if (!palOut) {
        free(rgba);
        free(block->rawBits);
        free(block);
        return -1;
    }

    unsigned char *src = rgba;
    unsigned short *dst = (unsigned short *)palOut;
    {
        unsigned int i;
        switch (pal->palFmt) {
            case GX_CLR_RGB565:
                for (i = 0; i < numColors; i++, src += 4) {
                    unsigned short v = (unsigned short)(
                        (((unsigned)src[0] >> 3) << 11) |
                        (((unsigned)src[1] >> 2) <<  5) |
                        (((unsigned)src[2] >> 3)       ));
                    *dst++ = (unsigned short)((v >> 8) | (v << 8));
                }
                break;

            case GX_CLR_RGB5A3:
                for (i = 0; i < numColors; i++, src += 4) {
                    unsigned short v;
                    if (src[3] >= 0xE8) {
                        v = (unsigned short)(0x8000 |
                            (((unsigned)src[0] >> 3) << 10) |
                            (((unsigned)src[1] >> 3) <<  5) |
                            (((unsigned)src[2] >> 3)       ));
                    } else {
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
                free(block->dfPtr);
                free(block);
                return -1;
        }
    }

    pal->palData = palOut;
    free(rgba);
    free(block->rawBits);
    free(block->dfPtr);
    free(block);
    return 0;
}

/* TCGetTplVersion */
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

/* Palette writers */
static int WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx)
{
    unsigned short fmtBE   = 0x0001;
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
    unsigned short fmtBE = 0x0002;
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

/* parsers and helpers */
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