CC      ?= gcc
CFLAGS  ?= -std=c99 -O2 -Wall -Wextra
LDLIBS  ?= -lm
ZIG     ?= zig

all: mapdump

mapdump: mapdump.c ../NavGrid/navgrid.c ../NavGrid/navgrid.h
	$(CC) $(CFLAGS) -I ../NavGrid -o $@ mapdump.c ../NavGrid/navgrid.c $(LDLIBS)

test: mapdump
	python3 test_mapdump.py /tmp/mapdump-test

# Cross-compiled for the game machine; no headers or runtime needed there.
win:
	mkdir -p bin-x64
	$(ZIG) cc -target x86_64-windows-gnu -O2 -w -I ../NavGrid \
		-o bin-x64/mapdump.exe mapdump.c ../NavGrid/navgrid.c

clean:
	rm -rf mapdump bin-x64

.PHONY: all test win clean
