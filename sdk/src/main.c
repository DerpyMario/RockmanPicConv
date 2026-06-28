/* src/main.c
   Program entry and script parsing / high-level orchestration
   (split from original single-file source)
*/
#include "../include/rockman.h"

#define SCRIPT_KW_PATH    "PATH"
#define SCRIPT_KW_NULL    "NULL"
#define SCRIPT_KW_FILE    "FILE"
#define SCRIPT_KW_IMAGE   "IMAGE"
#define SCRIPT_KW_PALETTE "PALETTE"
#define SCRIPT_KW_TEXTURE "TEXTURE"

#define SCRIPT_MAX_LINE  1024

int TCReadTplTextFile(TplState *state, const char *scriptPath)
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
                if (!state->images) {
                    fclose(fp);
                    return -1;
                }
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
                    snprintf(state->palettes[idx].tgaPath,
                             sizeof(state->palettes[idx].tgaPath),
                             "%s/%s", state->basePath, palFile);
                    state->palettes[idx].palFmt = (GXTlutFmt)ParseTlutFmt(fmtStr);
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
                snprintf(img->tgaPath, sizeof(img->tgaPath),
                         "%s/%s", state->basePath, texFile);
                img->texFmt = (GXTexFmt)ParseTexFmt(fmtStr);
                img->wrapS  = (GXTexWrapMode)ParseWrapMode(wrapSStr);
                img->wrapT  = (GXTexWrapMode)ParseWrapMode(wrapTStr);
                img->paletteIndex = paletteIdx;
            }
            imageIdx++;
            continue;
        }
    }

    fclose(fp);
    return 0;
}

/* Forward prototypes of TCWriteTplFile so main can call it */
int TCWriteTplFile(TplState *state, const char *outPcpPath);

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
    for (int i = 0; i < state.numImages; i++) {
        totalBytes += state.images[i].width * state.images[i].height * 4;
    }
    printf("O = %d\n", state.numImages);
    printf("Y : %d bytes\n", (totalBytes + 0x3FF) >> 10);

    if (TCWriteTplFile(&state, outPcpPath) != 0) {
        fprintf(stderr, "Error: failed to write PCP file '%s'\n", outPcpPath);
    }

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