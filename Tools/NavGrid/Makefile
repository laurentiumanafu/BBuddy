CC      ?= gcc
CFLAGS  ?= -std=c99 -O2 -Wall -Wextra
LDLIBS  ?= -lm
SRC      = navgrid.c adt.c

all: navbake test_navgrid

navbake: navbake.c $(SRC) navgrid.h adt.h
	$(CC) $(CFLAGS) -o $@ navbake.c $(SRC) $(LDLIBS)

test_navgrid: test_navgrid.c $(SRC) navgrid.h adt.h
	$(CC) $(CFLAGS) -o $@ test_navgrid.c $(SRC) $(LDLIBS)

test: test_navgrid
	./test_navgrid

clean:
	rm -f navbake test_navgrid *.o

.PHONY: all test clean
