/* include/rockman.h
   Shared declarations for RockmanPicConv MSVC6 project
*/
#ifndef ROCKMAN_H
#define ROCKMAN_H

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

#ifdef _MSC_VER
  typedef unsigned long uintptr_t;
  #define snprintf _snprintf
#endif

/* GX enums */
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

/* TGA header structure (packed) */
#pragma pack(push,1)
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

/* Image block produced by decoder */
typedef struct TgaImageBlock {
    unsigned char  *rawBits;
    unsigned int    rawSize;
    unsigned int    dfPtr;          /* palette pointer as integer */
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

/* Script/TPL structures */
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
    unsigned char *palData; /* 2 bytes per entry (big-endian) */
} TplPaletteEntry;

typedef struct TplState {
    char              basePath[512];
    int               numImages;
    int               numPalettes;
    TplImageEntry    *images;
    TplPaletteEntry  *palettes;
    int               version;
} TplState;

/* Forward declarations (from the decompiled source) */
int  DecodeTgaFile(const char *filename, TgaImageBlock **outBlock);
unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block, const char *filename);
unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block, const char *filename);
void AdjustTgaForPaletteOffset(TgaImageBlock *block);

int  TCReadTplTextFile(TplState *state, const char *scriptPath);
int  TCWriteTplFile(TplState *state, const char *outPcpPath);
int  TCSetTplPaletteValues(TplState *state, int paletteIdx);
int  TCGetTplVersion(const char *existingTplPath);
int  WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx);
int  WritePaletteBlockRGB5A3(FILE *fp, TplPaletteEntry *pal, int paletteIdx);

/* new encoders */
unsigned char *EncodeTextureToFormat(const unsigned char *rgba, unsigned short width, unsigned short height,
                                     GXTexFmt fmt, TplPaletteEntry *palette, int *outSize);

int ParseTexFmt(const char *s);
int ParseTlutFmt(const char *s);
int ParseWrapMode(const char *s);
int IsPowerOfTwo(unsigned int x);

#endif /* ROCKMAN_H */