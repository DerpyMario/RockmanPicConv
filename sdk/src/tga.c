/* src/tga.c
   TGA decoding and RGBA/alpha layer extraction
*/
#include "../include/rockman.h"

#define TGA_TYPE_NO_IMAGE          0
#define TGA_TYPE_COLOR_MAPPED      1
#define TGA_TYPE_TRUE_COLOR        2
#define TGA_TYPE_GRAYSCALE         3
#define TGA_TYPE_RLE_COLOR_MAPPED  9
#define TGA_TYPE_RLE_TRUE_COLOR   10
#define TGA_TYPE_RLE_GRAYSCALE    11

int DecodeTgaFile(const char *filename, TgaImageBlock **outBlock)
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

    block->dfPtr   = (unsigned int)(uintptr_t)palData;
    block->rawBits = rawBits;
    block->rawSize = rawSize;
    assert(block->dfPtr != 0 || hdr.colorMapType == 0);

    *outBlock = block;
    return 1;
}

unsigned char *CreateImageColorLayerFromTga(TgaImageBlock *block,
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
        case TGA_TYPE_GRAYSCALE:
        case TGA_TYPE_RLE_GRAYSCALE: {
            if (block->pixelDepth == 8) {
                for (unsigned int i = 0; i < numPixels; i++, src++) {
                    *dst++ = *src;
                    *dst++ = *src;
                    *dst++ = *src;
                    *dst++ = 0xFF;
                }
            } else if (block->pixelDepth == 16) {
                for (unsigned int i = 0; i < numPixels; i++, src += 2) {
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
        }
        case TGA_TYPE_TRUE_COLOR:
        case TGA_TYPE_RLE_TRUE_COLOR: {
            if (block->pixelDepth == 16) {
                for (unsigned int i = 0; i < numPixels; i++, src += 2) {
                    unsigned short px = (unsigned short)(src[0] | (src[1] << 8));
                    *dst++ = (unsigned char)(((px >> 10) & 0x1F) << 3);
                    *dst++ = (unsigned char)(((px >>  5) & 0x1F) << 3);
                    *dst++ = (unsigned char)(( px        & 0x1F) << 3);
                    *dst++ = 0xFF;
                }
            } else if (block->pixelDepth == 24) {
                for (unsigned int i = 0; i < numPixels; i++, src += 3) {
                    *dst++ = src[2];
                    *dst++ = src[1];
                    *dst++ = src[0];
                    *dst++ = 0xFF;
                }
            } else if (block->pixelDepth == 32) {
                for (unsigned int i = 0; i < numPixels; i++, src += 4) {
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
        }
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

    block->colorLayer = layer;
    return layer;
}

unsigned char *CreateImageAlphaLayerFromTga(TgaImageBlock *block,
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
        if (block->pixelDepth == 16) {
            for (unsigned int i = 0; i < numPixels; i++, src += 2)
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
            for (unsigned int i = 0; i < numPixels; i++, src += 4)
                *dst++ = src[3];
        } else {
            memset(dst, 0xFF, numPixels);
        }
    } else {
        memset(dst, 0xFF, numPixels);
    }

    block->alphaLayer = layer;
    return layer;
}

void AdjustTgaForPaletteOffset(TgaImageBlock *block)
{
    if (block->paletteOffset == 0)
        return;

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