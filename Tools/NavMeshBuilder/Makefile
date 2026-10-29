# Builds mmapbuild / mmapcheck against the Detour sources of a Navigation-C- checkout.
#
#   NAV_SRC=/tmp/navigation-build/Navigation make
#
# NAV_SRC points at the Navigation folder of https://github.com/Likon69/Navigation-C-
# (the same checkout build-navigation.sh uses). DT_POLYREF64 must match the DLL.
NAV_SRC ?= /tmp/navigation-build/Navigation
DETOUR   = $(NAV_SRC)/Detour/Source
CXX     ?= g++
CFLAGS  ?= -std=c++17 -O2 -w -DDT_POLYREF64
INCLUDES = -I $(NAV_SRC)/Detour/Include -I ../NavGrid
SOURCES  = $(DETOUR)/DetourAlloc.cpp $(DETOUR)/DetourAssert.cpp $(DETOUR)/DetourCommon.cpp \
           $(DETOUR)/DetourNavMesh.cpp $(DETOUR)/DetourNavMeshBuilder.cpp \
           $(DETOUR)/DetourNavMeshQuery.cpp $(DETOUR)/DetourNode.cpp

all: mmapbuild mmapcheck

../NavGrid/navgrid.o:
	$(CC) -std=c99 -O2 -c -I ../NavGrid ../NavGrid/navgrid.c -o $@

mmapbuild: mmapbuild.cpp ../NavGrid/navgrid.o
	$(CXX) $(CFLAGS) $(INCLUDES) -o $@ mmapbuild.cpp $(SOURCES) ../NavGrid/navgrid.o

mmapcheck: mmapcheck.cpp
	$(CXX) $(CFLAGS) $(INCLUDES) -o $@ mmapcheck.cpp $(SOURCES)

# Cross-compiled for the game machine. Detour is compiled in, so the exes are
# self-contained; -static-libgcc -static-libstdc++ keeps them free of a MinGW DLL.
ZIG ?= zig
win: bin-x64/mmapbuild.exe bin-x64/mmapcheck.exe

# navgrid.c is compiled as C (it is C99) and linked in, which needs the extern "C"
# guards navgrid.h carries.
../NavGrid/navgrid-win.o: ../NavGrid/navgrid.c ../NavGrid/navgrid.h
	$(ZIG) cc -target x86_64-windows-gnu -std=c99 -O2 -w -c -I ../NavGrid -o $@ ../NavGrid/navgrid.c

bin-x64/mmapbuild.exe: mmapbuild.cpp ../NavGrid/navgrid-win.o
	mkdir -p bin-x64
	$(ZIG) c++ -target x86_64-windows-gnu $(CFLAGS) -static-libgcc -static-libstdc++ \
		$(INCLUDES) -o $@ mmapbuild.cpp $(SOURCES) ../NavGrid/navgrid-win.o -lm

bin-x64/mmapcheck.exe: mmapcheck.cpp
	mkdir -p bin-x64
	$(ZIG) c++ -target x86_64-windows-gnu $(CFLAGS) -static-libgcc -static-libstdc++ \
		$(INCLUDES) -o $@ mmapcheck.cpp $(SOURCES) -lm

clean:
	rm -f mmapbuild mmapcheck ../NavGrid/navgrid.o ../NavGrid/navgrid-win.o
	rm -rf bin-x64

.PHONY: all win clean
