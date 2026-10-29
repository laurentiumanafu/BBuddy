"""Checks package PE identities and exact observed SIMD-copy address model only."""
from pathlib import Path
import struct
for name in ('WowBCtmInject.exe','WowBCtmProbe.dll'):
    b=(Path(__file__).parent/'native'/name).read_bytes()
    assert b[:2]==b'MZ'
    pe=struct.unpack_from('<I',b,0x3c)[0]
    assert b[pe:pe+4]==b'PE\0\0'
    assert struct.unpack_from('<H',b,pe+4)[0]==0x8664
    print(name,'AMD64 PE bytes=',len(b))
pattern=bytes.fromhex('F3410F6F4409F04883C004F30F7F41F0')
assert len(pattern)==16
mask=(1<<64)-1
r9=0xFFFFFFFFBEDA0000
for dest,src in [(0x27BE0CA23C0,0x27B9FA423C0),(0x27BE0CA2280,0x27B9FA42280)]:
    assert (dest+r9)&mask==src
print('Observed copy-pattern address model passed; no Windows APIs or live process exercised.')
