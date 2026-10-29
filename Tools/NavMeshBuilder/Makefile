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

clean:
	rm -f mmapbuild mmapcheck ../NavGrid/navgrid.o

.PHONY: all clean
