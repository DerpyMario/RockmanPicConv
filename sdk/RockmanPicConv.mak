# nmake makefile for Visual C++ 6.0
CC=cl
CFLAGS=/nologo /W3 /Zi /Od /Iinclude
LINKFLAGS=/nologo
OBJS=src\\main.obj src\\tga.obj src\\tpl.obj

all: RockmanPicConv.exe

RockmanPicConv.exe: $(OBJS)
	$(CC) $(LINKFLAGS) $(OBJS)

src\\main.obj: src\\main.c include\\rockman.h
	$(CC) $(CFLAGS) /c src\\main.c /Fo src\\main.obj

src\\tga.obj: src\\tga.c include\\rockman.h
	$(CC) $(CFLAGS) /c src\\tga.c /Fo src\\tga.obj

src\\tpl.obj: src\\tpl.c include\\rockman.h
	$(CC) $(CFLAGS) /c src\\tpl.c /Fo src\\tpl.obj

clean:
	if exist src\\*.obj del src\\*.obj
	if exist RockmanPicConv.exe del RockmanPicConv.exe