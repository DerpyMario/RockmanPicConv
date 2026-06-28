RockmanPicConv — VC6 project (multi-file)
=======================================

Files added:
 - include/rockman.h
 - src/main.c
 - src/tga.c
 - src/tpl.c
 - RockmanPicConv.mak
 - README.md

Build with Visual C++ 6.0 IDE:
 - Create a new Win32 Console project (empty) named RockmanPicConv.
 - Add the files under src/ to the project.
 - Add the include/ directory to Project -> Settings -> C/C++ -> Additional include directories.
 - Build (F7).

Build with nmake (command-line):
 - Open Visual C++ 6.0 Command Prompt (vcvars) or run vcvars32.bat.
 - cd to project root.
 - nmake /f RockmanPicConv.mak

Notes:
 - Implemented encoders for RGB565, RGB5A3, CI4, CI8 and a basic CMPR (DXT1-like) block compressor.
 - Texture encoder performs a Morton/twiddle ordering for CMPR blocks; other formats are encoded linearly (the code includes routines you can adapt to the exact swizzle used by specific tools).
 - If you need exact byte-for-byte compatibility with original RockmanPicConv's TPL output, provide:
    - a sample .pcp/.tpl produced by the original tool OR
    - a script and TGA set so I can test and adjust the swizzle/tile ordering and CMPR parameters.

Next steps I can do for you:
 - Produce a complete Visual Studio 6 .dsp/.dsw workspace file (I can generate .dsp/.dsw text for the project).
 - Tune the twiddle/swi z z le ordering to match a reference TPL and verify produced PCP files in a GameCube/TPL tool.
 - Optimize and improve CMPR quality (cluster-fit or k-means per-block).
 - Add RLE TGA decoding support (currently the TGA loader reads raw image data; RLE types are not decoded here).

Tell me which of the next steps you want me to do now; if you want the .dsp/.dsw workspace produced I will add those files next.