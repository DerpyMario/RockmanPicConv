/* pcp_parse.c
 * Simple PCP header parser/inspector.
 *
 * Build with Visual C++ 6.0 or any modern compiler:
 *   cl /Zi /W3 pcp_parse.c
 *
 * Usage:
 *   pcp_parse EnvMap.pcp
 *
 * It prints:
 *   - magic
 *   - image count
 *   - per-image descriptors (format, wrapS, wrapT, numMips, width, height, paletteIndex)
 *
 * The parser is conservative and won't attempt to decode palette/pixel blocks beyond the header + descriptors.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned int read_u32_be(FILE *f) {
    unsigned char b[4];
    if (fread(b,1,4,f) != 4) return 0;
    return (b[0]<<24) | (b[1]<<16) | (b[2]<<8) | b[3];
}

static unsigned short read_u16_be_from_buf(const unsigned char *p) {
    return (unsigned short)((p[0]<<8) | p[1]);
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <file.pcp>\n", argv[0]);
        return 1;
    }

    const char *path = argv[1];
    FILE *fp = fopen(path, "rb");
    if (!fp) { perror("fopen"); return 1; }

    unsigned int magic = read_u32_be(fp);
    unsigned int imgCount = read_u32_be(fp);

    printf("Magic (BE): 0x%08X\n", magic);
    printf("Magic (ASCII): '%c%c%c%c'\n",
           (char)((magic>>24)&0xFF),(char)((magic>>16)&0xFF),
           (char)((magic>>8)&0xFF),(char)((magic>>0)&0xFF));
    printf("Image count (BE): %u\n\n", imgCount);

    if (magic != 0x50494300) {
        fprintf(stderr, "Warning: magic != 'PIC\\0' (0x50494300)\n");
    }

    /* Read per-image descriptors (16 bytes each as in reconstructed format) */
    for (unsigned int i = 0; i < imgCount; ++i) {
        unsigned char desc[16];
        size_t got = fread(desc, 1, sizeof(desc), fp);
        if (got != sizeof(desc)) {
            fprintf(stderr, "Unexpected EOF while reading descriptor %u\n", i);
            break;
        }
        int texFmt = (int)desc[0];
        int wrapS  = (int)desc[1];
        int wrapT  = (int)desc[2];
        int numMips= (int)desc[3];
        unsigned short width  = read_u16_be_from_buf(&desc[4]);
        unsigned short height = read_u16_be_from_buf(&desc[6]);
        int paletteIndex = (int)desc[8];

        printf("Image %u: texFmt=%d wrapS=%d wrapT=%d numMips=%d width=%u height=%u paletteIdx=%d\n",
               i, texFmt, wrapS, wrapT, numMips, width, height, paletteIndex);
    }

    fclose(fp);
    return 0;
}