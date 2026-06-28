/* src/tpl.c
   Palette handling, palette output and texture encoders
*/
#include "../include/rockman.h"

/* Small helpers */
static unsigned short ToBE16(unsigned short v) {
    return (unsigned short)((v >> 8) | (v << 8));
}
static unsigned int  ToBE32(unsigned int v) {
    return ((v >> 24) & 0xFF) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | ((v << 24) & 0xFF000000);
}

/* Twiddle (Morton) interleave for (x,y) producing a linear index.
   This is a commonly-used ordering for GameCube textures.
*/
static unsigned int morton2(unsigned int x, unsigned int y)
{
    unsigned int z = 0;
    unsigned int bit = 1;
    for (unsigned int i = 0; i < 16; i++) {
        z |= ((y & bit) ? (1u << (2*i+1)) : 0);
        z |= ((x & bit) ? (1u << (2*i  )) : 0);
        bit <<= 1;
    }
    return z;
}

/* Find nearest palette index (simple squared Euclidean distance) */
static int find_nearest_palette_index(const unsigned char *palRGBA, int palCount,
                                      unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    int best = 0;
    unsigned int bestDist = 0xFFFFFFFFu;
    const unsigned char *p = palRGBA;
    for (int i = 0; i < palCount; i++, p += 4) {
        int dr = (int)p[0] - (int)r;
        int dg = (int)p[1] - (int)g;
        int db = (int)p[2] - (int)b;
        int da = (int)p[3] - (int)a;
        unsigned int dist = (unsigned int)(dr*dr + dg*dg + db*db + da*da);
        if (dist < bestDist) { bestDist = dist; best = i; }
        if (bestDist == 0) break;
    }
    return best;
}

/* Encode RGBA8 -> RGB565 (big-endian 2 bytes per pixel) */
static unsigned char *EncodeRGB565(const unsigned char *rgba, unsigned short width, unsigned short height, int *outSize)
{
    unsigned int num = (unsigned int)width * height;
    unsigned char *out = (unsigned char *)malloc(num * 2);
    if (!out) return NULL;
    unsigned char *dst = out;
    for (unsigned int i = 0; i < num; i++, rgba += 4) {
        unsigned short v = (unsigned short)((((unsigned)rgba[0] >> 3) << 11) |
                                           (((unsigned)rgba[1] >> 2) << 5) |
                                           (((unsigned)rgba[2] >> 3)      ));
        unsigned short be = ToBE16(v);
        *dst++ = (unsigned char)(be >> 8);
        *dst++ = (unsigned char)(be & 0xFF);
    }
    if (outSize) *outSize = num * 2;
    return out;
}

/* Encode RGBA8 -> RGB5A3 (big-endian 2 bytes per pixel) */
static unsigned char *EncodeRGB5A3(const unsigned char *rgba, unsigned short width, unsigned short height, int *outSize)
{
    unsigned int num = (unsigned int)width * height;
    unsigned char *out = (unsigned char *)malloc(num * 2);
    if (!out) return NULL;
    unsigned char *dst = out;
    for (unsigned int i = 0; i < num; i++, rgba += 4) {
        unsigned short v;
        if (rgba[3] >= 0xE8) {
            v = (unsigned short)(0x8000 |
                (((unsigned)rgba[0] >> 3) << 10) |
                (((unsigned)rgba[1] >> 3) <<  5) |
                (((unsigned)rgba[2] >> 3)       ));
        } else {
            v = (unsigned short)(
                (((unsigned)rgba[3] >> 5) << 12) |
                (((unsigned)rgba[0] >> 4) <<  8) |
                (((unsigned)rgba[1] >> 4) <<  4) |
                (((unsigned)rgba[2] >> 4)       ));
        }
        unsigned short be = ToBE16(v);
        *dst++ = (unsigned char)(be >> 8);
        *dst++ = (unsigned char)(be & 0xFF);
    }
    if (outSize) *outSize = num * 2;
    return out;
}

/* Encode RGBA8 -> CI8: look up each pixel in palette (palData is big-endian 2-byte entries
   but we also need the palette's RGBA colors — TCSetTplPaletteValues constructs palData
   from a palette TGA; for CI encoding we expect caller to supply palette->palData is 2-byte
   entries, but we require RGBA lookup — therefore TCSetTplPaletteValues will keep an rgba
   temporary. For simplicity, when pal->palData exists we will reconstruct approximate RGBA
   from that pal->palData (RGB565 or RGB5A3 decoding). For exact match you should use the
   original palette TGA to build rgba palette array — here we decode from pal->palData. */
static void decode_pal_entry_to_rgba(unsigned short be, unsigned char *outRGBA, int palFmt)
{
    unsigned short v = (unsigned short)((be >> 8) | (be << 8));
    if (palFmt == GX_CLR_RGB565) {
        unsigned char r = (unsigned char)(((v >> 11) & 0x1F) << 3);
        unsigned char g = (unsigned char)(((v >> 5) & 0x3F) << 2);
        unsigned char b = (unsigned char)((v & 0x1F) << 3);
        outRGBA[0] = r; outRGBA[1] = g; outRGBA[2] = b; outRGBA[3] = 0xFF;
    } else if (palFmt == GX_CLR_RGB5A3) {
        if (v & 0x8000) {
            unsigned char r = (unsigned char)(((v >> 10) & 0x1F) << 3);
            unsigned char g = (unsigned char)(((v >> 5) & 0x1F) << 3);
            unsigned char b = (unsigned char)((v & 0x1F) << 3);
            outRGBA[0] = r; outRGBA[1] = g; outRGBA[2] = b; outRGBA[3] = 0xFF;
        } else {
            unsigned char a = (unsigned char)(((v >> 12) & 0x7) << 5);
            unsigned char r = (unsigned char)(((v >> 8) & 0xF) << 4);
            unsigned char g = (unsigned char)(((v >> 4) & 0xF) << 4);
            unsigned char b = (unsigned char)((v & 0xF) << 4);
            outRGBA[0] = r; outRGBA[1] = g; outRGBA[2] = b; outRGBA[3] = a;
        }
    } else {
        outRGBA[0]=outRGBA[1]=outRGBA[2]=0; outRGBA[3]=0xFF;
    }
}

/* Build an RGBA palette array from pal->palData (2 bytes per entry) */
static unsigned char *build_rgba_palette_from_paldata(TplPaletteEntry *pal)
{
    if (!pal || !pal->palData || pal->numColors <= 0) return NULL;
    unsigned char *rgba = (unsigned char *)malloc(pal->numColors * 4);
    if (!rgba) return NULL;
    unsigned short *beArr = (unsigned short *)pal->palData;
    for (int i = 0; i < pal->numColors; i++) {
        unsigned short be = beArr[i];
        unsigned char tmp[4];
        decode_pal_entry_to_rgba(be, tmp, pal->palFmt);
        unsigned char *dst = rgba + i*4;
        dst[0] = tmp[0]; dst[1] = tmp[1]; dst[2] = tmp[2]; dst[3] = tmp[3];
    }
    return rgba;
}

/* Encode CI8 (1 byte per pixel) */
static unsigned char *EncodeCI8(const unsigned char *rgba, unsigned short width, unsigned short height,
                                TplPaletteEntry *palette, int *outSize)
{
    int palCount = palette ? palette->numColors : 0;
    unsigned char *palRGBA = build_rgba_palette_from_paldata(palette);
    if (!palRGBA && palCount > 0) {
        /* fallback: can't build palette */
        if (outSize) *outSize = 0;
        return NULL;
    }

    unsigned int num = (unsigned int)width * height;
    unsigned char *out = (unsigned char *)malloc(num);
    if (!out) { free(palRGBA); return NULL; }

    for (unsigned int i = 0; i < num; i++, rgba += 4) {
        int idx = 0;
        if (palCount > 0) {
            /* try exact match first */
            int found = -1;
            for (int p = 0; p < palCount; p++) {
                unsigned char *pe = palRGBA + p*4;
                if (pe[0] == rgba[0] && pe[1] == rgba[1] && pe[2] == rgba[2] && pe[3] == rgba[3]) { found = p; break; }
            }
            if (found >= 0) idx = found;
            else idx = find_nearest_palette_index(palRGBA, palCount, rgba[0], rgba[1], rgba[2], rgba[3]);
        } else {
            /* no palette, quantize to 256-color space (very simple) */
            idx = ((unsigned)rgba[0] >> 5) << 5 | ((unsigned)rgba[1] >> 5) << 2 | ((unsigned)rgba[2] >> 6);
        }
        out[i] = (unsigned char)idx;
    }

    free(palRGBA);
    if (outSize) *outSize = num;
    return out;
}

/* Encode CI4 (4bpp packed: two indices per byte: high nibble = first pixel) */
static unsigned char *EncodeCI4(const unsigned char *rgba, unsigned short width, unsigned short height,
                                TplPaletteEntry *palette, int *outSize)
{
    int palCount = palette ? palette->numColors : 0;
    unsigned char *palRGBA = build_rgba_palette_from_paldata(palette);
    if (!palRGBA && palCount > 0) { if (outSize) *outSize=0; return NULL; }

    unsigned int num = (unsigned int)width * height;
    unsigned int outBytes = (num + 1) / 2;
    unsigned char *out = (unsigned char *)malloc(outBytes);
    if (!out) { free(palRGBA); return NULL; }
    memset(out, 0, outBytes);

    unsigned int idxOut = 0;
    for (unsigned int i = 0; i < num; i++, rgba += 4) {
        int idx;
        if (palCount > 0) {
            int found = -1;
            for (int p = 0; p < palCount; p++) {
                unsigned char *pe = palRGBA + p*4;
                if (pe[0] == rgba[0] && pe[1] == rgba[1] && pe[2] == rgba[2] && pe[3] == rgba[3]) { found = p; break; }
            }
            if (found >= 0) idx = found;
            else idx = find_nearest_palette_index(palRGBA, palCount, rgba[0], rgba[1], rgba[2], rgba[3]);
        } else {
            idx = ((unsigned)rgba[0] >> 4) & 0xF;
        }
        if ((i & 1) == 0) {
            out[idxOut] = (unsigned char)((idx & 0xF) << 4);
        } else {
            out[idxOut] |= (unsigned char)(idx & 0xF);
            idxOut++;
        }
    }

    free(palRGBA);
    if (outSize) *outSize = (int)outBytes;
    return out;
}

/* Simple DXT1/CMPR encoder for 4x4 block: choose min/max color, compute palette */
static void compress_block_dxt1(const unsigned char *rgba, int stride, unsigned char *out8)
{
    /* stride = rgba row stride in bytes (width * 4) */
    unsigned char mins[3] = {255,255,255}, maxs[3] = {0,0,0};
    const unsigned char *p = rgba;
    for (int y = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++) {
            mins[0] = (unsigned char)min(mins[0], p[0]);
            mins[1] = (unsigned char)min(mins[1], p[1]);
            mins[2] = (unsigned char)min(mins[2], p[2]);
            maxs[0] = (unsigned char)max(maxs[0], p[0]);
            maxs[1] = (unsigned char)max(maxs[1], p[1]);
            maxs[2] = (unsigned char)max(maxs[2], p[2]);
            p += 4;
        }
        p += (stride - 4*4);
    }

    /* endpoints: pack to RGB565 */
    unsigned short c0 = (unsigned short)((((unsigned)maxs[0] >> 3) << 11) |
                                         (((unsigned)maxs[1] >> 2) << 5) |
                                         (((unsigned)maxs[2] >> 3)      ));
    unsigned short c1 = (unsigned short)((((unsigned)mins[0] >> 3) << 11) |
                                         (((unsigned)mins[1] >> 2) << 5) |
                                         (((unsigned)mins[2] >> 3)      ));
    unsigned short c0be = ToBE16(c0);
    unsigned short c1be = ToBE16(c1);
    /* write endpoints little-endian? For TPL/GC, blocks are stored as BE words in some tools.
       Historically DXT1 stores endpoints little-endian; however TPL CMPR block stores 2 16-bit
       words and a 32-bit index. We'll use little-endian endpoints to be compatible with many tools. */
    out8[0] = (unsigned char)(c0 & 0xFF);
    out8[1] = (unsigned char)(c0 >> 8);
    out8[2] = (unsigned char)(c1 & 0xFF);
    out8[3] = (unsigned char)(c1 >> 8);

    /* compute palette colors in 24-bit */
    unsigned char col[4][3];
    /* decode c0, c1 to rgb */
    col[0][0] = (unsigned char)(((c0 >> 11) & 0x1F) * 255 / 31);
    col[0][1] = (unsigned char)(((c0 >> 5) & 0x3F) * 255 / 63);
    col[0][2] = (unsigned char)(((c0) & 0x1F) * 255 / 31);
    col[1][0] = (unsigned char)(((c1 >> 11) & 0x1F) * 255 / 31);
    col[1][1] = (unsigned char)(((c1 >> 5) & 0x3F) * 255 / 63);
    col[1][2] = (unsigned char)(((c1) & 0x1F) * 255 / 31);

    if (c0 > c1) {
        /* four-color block: interpolate */
        for (int i = 0; i < 3; i++) {
            col[2][i] = (unsigned char)((2*col[0][i] + col[1][i]) / 3);
            col[3][i] = (unsigned char)((col[0][i] + 2*col[1][i]) / 3);
        }
    } else {
        /* three-color block: interpolate, last is transparent black */
        for (int i = 0; i < 3; i++) {
            col[2][i] = (unsigned char)((col[0][i] + col[1][i]) / 2);
            col[3][i] = 0;
        }
    }

    /* compute 2-bit indices for each pixel, pack into 32-bit little-endian */
    unsigned int idxBits = 0;
    unsigned int bitPos = 0;
    p = rgba;
    for (int y = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++) {
            /* find nearest color */
            int best = 0;
            unsigned int bestDist = 0xFFFFFFFFu;
            for (int k = 0; k < 4; k++) {
                int dr = (int)p[0] - (int)col[k][0];
                int dg = (int)p[1] - (int)col[k][1];
                int db = (int)p[2] - (int)col[k][2];
                unsigned int dist = (unsigned int)(dr*dr + dg*dg + db*db);
                if (dist < bestDist) { bestDist = dist; best = k; }
            }
            idxBits |= ((unsigned int)best) << bitPos;
            bitPos += 2;
            p += 4;
        }
        p += (stride - 4*4);
    }

    /* write index bits as little-endian 32-bit */
    out8[4] = (unsigned char)(idxBits & 0xFF);
    out8[5] = (unsigned char)((idxBits >> 8) & 0xFF);
    out8[6] = (unsigned char)((idxBits >> 16) & 0xFF);
    out8[7] = (unsigned char)((idxBits >> 24) & 0xFF);
}

/* Encode RGBA -> CMPR (DXT1) with block-level twiddle ordering */
static unsigned char *EncodeCMPR(const unsigned char *rgba, unsigned short width, unsigned short height, int *outSize)
{
    unsigned short wBlocks = (width + 3) / 4;
    unsigned short hBlocks = (height + 3) / 4;
    unsigned int outBytes = (unsigned int)wBlocks * hBlocks * 8;
    unsigned char *out = (unsigned char *)malloc(outBytes);
    if (!out) return NULL;
    memset(out, 0, outBytes);

    int blockSize = 8;
    unsigned char blockbuf[8];
    for (unsigned short by = 0; by < hBlocks; by++) {
        for (unsigned short bx = 0; bx < wBlocks; bx++) {
            /* build 4x4 block pixel array (pad edges with zero/last pixel) */
            unsigned char blockPixels[4*4*4];
            memset(blockPixels, 0, sizeof(blockPixels));
            for (int y = 0; y < 4; y++) {
                int srcY = (int)by*4 + y;
                for (int x = 0; x < 4; x++) {
                    int srcX = (int)bx*4 + x;
                    unsigned char *dst = blockPixels + (y*4 + x)*4;
                    if (srcX < width && srcY < height) {
                        const unsigned char *src = rgba + (srcY * width + srcX)*4;
                        dst[0]=src[0]; dst[1]=src[1]; dst[2]=src[2]; dst[3]=src[3];
                    } else {
                        dst[0]=dst[1]=dst[2]=0; dst[3]=0xFF;
                    }
                }
            }
            compress_block_dxt1(blockPixels, 4*4, blockbuf);

            /* compute twiddled block index (block coordinates) */
            unsigned int mort = morton2(bx, by);
            unsigned int blockIdx = mort;
            unsigned int outOffset = blockIdx * blockSize;
            if (outOffset + 8 <= outBytes) {
                memcpy(out + outOffset, blockbuf, 8);
            } else {
                /* fallback sequential if morton too large */
                unsigned int seq = (unsigned int)by * wBlocks + bx;
                memcpy(out + seq * 8, blockbuf, 8);
            }
        }
    }

    if (outSize) *outSize = (int)outBytes;
    return out;
}

/* Top-level encoder: chooses implementation by format */
unsigned char *EncodeTextureToFormat(const unsigned char *rgba, unsigned short width, unsigned short height,
                                     GXTexFmt fmt, TplPaletteEntry *palette, int *outSize)
{
    if (!rgba) return NULL;
    switch (fmt) {
        case GX_TF_RGB565:
            return EncodeRGB565(rgba, width, height, outSize);
        case GX_TF_RGB5A3:
            return EncodeRGB5A3(rgba, width, height, outSize);
        case GX_TF_CI8:
            return EncodeCI8(rgba, width, height, palette, outSize);
        case GX_TF_CI4:
            return EncodeCI4(rgba, width, height, palette, outSize);
        case GX_TF_CMPR:
            return EncodeCMPR(rgba, width, height, outSize);
        case GX_TF_RGBA8: {
            /* pass-through RGBA8 */
            unsigned int num = (unsigned int)width * height;
            unsigned char *out = (unsigned char *)malloc(num * 4);
            if (!out) return NULL;
            memcpy(out, rgba, num * 4);
            if (outSize) *outSize = num * 4;
            return out;
        }
        default:
            return NULL;
    }
}

/* TCSetTplPaletteValues: decode palette TGA and convert to palData (2 bytes/entry) */
int TCSetTplPaletteValues(TplState *state, int paletteIdx)
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
                unsigned short v = (unsigned short)(
                    (((unsigned)src[0] >> 3) << 11) |
                    (((unsigned)src[1] >> 2) <<  5) |
                    (((unsigned)src[2] >> 3)       ));
                *dst++ = ToBE16(v);
            }
            break;

        case GX_CLR_RGB5A3:
            for (unsigned int i = 0; i < numColors; i++, src += 4) {
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
                *dst++ = ToBE16(v);
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

/* Write palette blocks in TPL file */
int WritePaletteBlockRGB565(FILE *fp, TplPaletteEntry *pal, int paletteIdx)
{
    unsigned short fmtBE   = ToBE16(0x0001);  /* GX_TL_RGB565 */
    unsigned int   cntBE   = ToBE32((unsigned int)pal->numColors);
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

int WritePaletteBlockRGB5A3(FILE *fp, TplPaletteEntry *pal, int paletteIdx)
{
    unsigned short fmtBE = ToBE16(0x0002);  /* GX_TL_RGB5A3 */
    unsigned int   cntBE = ToBE32((unsigned int)pal->numColors);
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

/* TCWriteTplFile: similar to original, but uses EncodeTextureToFormat for each image */
int TCWriteTplFile(TplState *state, const char *outPcpPath)
{
    /* First pass: decode images and create color/alpha layers */
    for (int i = 0; i < state->numImages; i++) {
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
                "TCWriteTplFile: since width %d is not a power of two, wrapS mode must be GX_CLAMP for image %d in script file\n",
                img->width, i);
            img->wrapS = GX_CLAMP;
        }
        if (!IsPowerOfTwo(img->height) && img->wrapT != GX_CLAMP) {
            fprintf(stderr,
                "TCWriteTplFile: since height %d is not a power of two, wrapT mode must be GX_CLAMP for image %d in script file\n",
                img->height, i);
            img->wrapT = GX_CLAMP;
        }

        img->colorData = CreateImageColorLayerFromTga(block, img->tgaPath);
        img->alphaData = CreateImageAlphaLayerFromTga(block, img->tgaPath);

        if (img->colorData == NULL || img->alphaData == NULL) {
            free(block->rawBits);
            free((void *)(uintptr_t)block->dfPtr);
            free(block);
            return -1;
        }

        if (block->colorMapDepth != 0)
            AdjustTgaForPaletteOffset(block);

        free(block->rawBits);
        free((void *)(uintptr_t)block->dfPtr);
        free(block);
    }

    /* Second pass: palettes */
    for (int p = 0; p < state->numPalettes; p++) {
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

    /* PCP header */
    unsigned int magic = 0x50494300u;
    fwrite(&magic, 4, 1, fp);

    unsigned int imgCount = (unsigned int)state->numImages;
    unsigned char cnt_be[4] = {
        (imgCount >> 24) & 0xFF,
        (imgCount >> 16) & 0xFF,
        (imgCount >>  8) & 0xFF,
        (imgCount >>  0) & 0xFF
    };
    fwrite(cnt_be, 4, 1, fp);

    /* per-image descriptors */
    for (int i = 0; i < state->numImages; i++) {
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

    /* palette blocks */
    for (int p = 0; p < state->numPalettes; p++) {
        TplPaletteEntry *pal = &state->palettes[p];
        if (pal->palFmt == GX_CLR_RGB565) {
            WritePaletteBlockRGB565(fp, pal, p);
        } else if (pal->palFmt == GX_CLR_RGB5A3) {
            WritePaletteBlockRGB5A3(fp, pal, p);
        }
    }

    /* pixel data blocks: encode per-image into target GX format and write the encoded bytes */
    for (int i = 0; i < state->numImages; i++) {
        TplImageEntry *img = &state->images[i];
        if (!img->colorData) continue;

        TplPaletteEntry *pal = NULL;
        if (img->paletteIndex >= 0 && img->paletteIndex < state->numPalettes)
            pal = &state->palettes[img->paletteIndex];

        int encSize = 0;
        unsigned char *enc = EncodeTextureToFormat(img->colorData, img->width, img->height, img->texFmt, pal, &encSize);
        if (!enc) {
            /* fallback: write raw RGBA8 */
            unsigned int numPx = (unsigned int)img->width * img->height;
            fwrite(img->colorData, numPx * 4, 1, fp);
        } else {
            fwrite(enc, 1, encSize, fp);
            free(enc);
        }
    }

    fclose(fp);
    return 0;
}

/* small parse helpers for script tokens (string->enum) */
int ParseTexFmt(const char *s)
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

int ParseTlutFmt(const char *s)
{
    if (!s) return GX_CLR_INVALID;
    if (strcmp(s, "RGB565") == 0) return GX_CLR_RGB565;
    if (strcmp(s, "RGB5A3") == 0) return GX_CLR_RGB5A3;
    if (strcmp(s, "IA8"   ) == 0) return GX_CLR_IA8;
    return GX_CLR_INVALID;
}

int ParseWrapMode(const char *s)
{
    if (!s) return GX_WRAP_INVALID;
    if (strcmp(s, "GX_CLAMP" ) == 0) return GX_CLAMP;
    if (strcmp(s, "GX_REPEAT") == 0) return GX_REPEAT;
    if (strcmp(s, "GX_MIRROR") == 0) return GX_MIRROR;
    return GX_WRAP_INVALID;
}

int IsPowerOfTwo(unsigned int x)
{
    return (x > 0) && ((x & (x - 1)) == 0);
}

/* TCGetTplVersion (unchanged from original) */
int TCGetTplVersion(const char *existingTplPath)
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
    if (magic == 0x50494300u) {
        unsigned int ver;
        if (fread(&ver, 4, 1, fp) == 1)
            version = (int)ver;
    }
    fclose(fp);
    return version;
}