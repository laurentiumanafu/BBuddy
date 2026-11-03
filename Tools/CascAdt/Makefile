# Builds cascdump - the CASC ADT extractor - plus a self-test on generated data.
#
#   make            build ./cascdump
#   make test       build it and run it against a synthetic store
#   make win        cross-build bin-x64/cascdump.exe (needs zig on PATH)
CC      ?= cc
CFLAGS  ?= -std=c99 -O2 -Wall -Wextra -Wno-unused-parameter -Wno-implicit-fallthrough
INCLUDES = -I ../NavGrid -I zlib

NAVGRID  = ../NavGrid/adt.c ../NavGrid/navgrid.c
ZLIB     = zlib/adler32.c zlib/crc32.c zlib/inflate.c zlib/inftrees.c zlib/inffast.c zlib/zutil.c
SOURCES  = cascdump.c $(NAVGRID) $(ZLIB)

all: cascdump

cascdump: cascdump.c ../NavGrid/adt.c ../NavGrid/navgrid.c ../NavGrid/adt.h ../NavGrid/navgrid.h $(ZLIB)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(SOURCES) -lm

win: bin-x64/cascdump.exe

bin-x64/cascdump.exe: cascdump.c ../NavGrid/adt.c ../NavGrid/navgrid.c $(ZLIB)
	mkdir -p bin-x64
	zig cc -target x86_64-windows-gnu -O2 -w $(INCLUDES) -o $@ $(SOURCES)
	rm -f bin-x64/cascdump.pdb

# Builds a synthetic CASC store, extracts it, and checks that the tiles, their
# positions and the terrain heights come back exactly as they went in.
test: cascdump
	rm -rf /tmp/cascdump-test
	rm -rf /tmp/cascdump-test/carved
	python3 make_test_casc.py /tmp/cascdump-test
	mkdir -p /tmp/cascdump-test/out
	./cascdump list --data /tmp/cascdump-test/data --verbose
	@./cascdump list --data /tmp/cascdump-test/data | grep -q "entries: 7" || (echo "FAIL: expected 7 entries across the four index layouts"; ./cascdump list --data /tmp/cascdump-test/data; exit 1)
	./cascdump dump --data /tmp/cascdump-test/data --out /tmp/cascdump-test/out --ref -100,-100,50
	@test -f /tmp/cascdump-test/out/1_32_32.adt || (echo "FAIL: tile (32,32) was not written"; exit 1)
	@test -f /tmp/cascdump-test/out/1_32_34.adt || (echo "FAIL: chunk-position tile (32,34) was not written"; exit 1)
	@test -f /tmp/cascdump-test/out/unknown_a2a2a2a2.adt || (echo "FAIL: tile with no position data was not reported"; exit 1)
	@grep -q "1_32_32.adt,32,32,placement,256,.*,50.00" /tmp/cascdump-test/out/adt-index.csv || (echo "FAIL: tile (32,32) height is wrong"; exit 1)
	@grep -q "1_32_34.adt,32,34,chunkpos" /tmp/cascdump-test/out/adt-index.csv || (echo "FAIL: tile (32,34) did not come from chunk positions"; exit 1)
	@test -f /tmp/cascdump-test/out/alternates/1_32_32__b1b1b1.adt || (echo "FAIL: competing tile was dropped instead of kept as an alternate"; exit 1)
	@./cascdump carve --data /tmp/cascdump-test/broken --out /tmp/cascdump-test/carved --ref -100,-100,50
	@test -f /tmp/cascdump-test/carved/1_32_32.adt || (echo "FAIL: carving did not find the terrain"; exit 1)
	@test -f /tmp/cascdump-test/carved/1_32_34.adt || (echo "FAIL: carving missed the chunk-position tile"; exit 1)
	@test -d /tmp/cascdump-test/carved/alternates || (echo "FAIL: carving dropped the competing continent"; exit 1)
	@echo "OK: index layouts, BLTE frames, tile solving, alternates and heights all check out"
	@echo "OK: carving finds the same terrain from a store whose index says nothing"

clean:
	rm -f cascdump bin-x64/cascdump.exe

.PHONY: all win test clean
