CC      ?= gcc
CFLAGS  ?= -std=c99 -O2 -Wall -Wextra
LDLIBS  ?= -lm
ZIG     ?= zig
SRC      = navgrid.c adt.c

all: navbake test_navgrid

navbake: navbake.c $(SRC) navgrid.h adt.h
	$(CC) $(CFLAGS) -o $@ navbake.c $(SRC) $(LDLIBS)

test_navgrid: test_navgrid.c $(SRC) navgrid.h adt.h
	$(CC) $(CFLAGS) -o $@ test_navgrid.c $(SRC) $(LDLIBS)

test: test_navgrid
	./test_navgrid

# Cross-compiled for the game machine; the tools there need no toolchain.
win: bin-x64/navbake.exe bin-x64/test_navgrid.exe

bin-x64/navbake.exe: navbake.c $(SRC) navgrid.h adt.h
	mkdir -p bin-x64
	$(ZIG) cc -target x86_64-windows-gnu -O2 -w -o $@ navbake.c $(SRC) -lm

bin-x64/test_navgrid.exe: test_navgrid.c $(SRC) navgrid.h adt.h
	mkdir -p bin-x64
	$(ZIG) cc -target x86_64-windows-gnu -O2 -w -o $@ test_navgrid.c $(SRC) -lm

clean:
	rm -f navbake test_navgrid *.o
	rm -rf bin-x64

.PHONY: all test win clean
