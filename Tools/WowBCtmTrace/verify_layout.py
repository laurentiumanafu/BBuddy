"""Offline Windows AMD64 layout/DR7 model, not an OS or live test."""
import ctypes as c

U16, U32, U64 = c.c_uint16, c.c_uint32, c.c_uint64
class Context(c.Structure):
    _fields_ = [(f'home{i}', U64) for i in range(6)] + [
        ('flags', U32), ('mxcsr', U32), *[(f'seg{i}', U16) for i in range(6)],
        ('eflags', U32), *[(f'dr{i}', U64) for i in (0,1,2,3,6,7)],
        *[(n, U64) for n in ('rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi')],
        *[(f'r{i}', U64) for i in range(8,16)], ('rip', U64),
        ('xmm_save', c.c_byte * 512), ('vectors', c.c_byte * (26*16)),
        ('vector_control', U64), ('debug_control', U64), ('last_branch_to', U64),
        ('last_branch_from', U64), ('last_exception_to', U64), ('last_exception_from', U64)]
class ExceptionRecord(c.Structure):
    _fields_ = [('code', U32), ('flags', U32), ('record', U64), ('address', U64),
                ('number', U32), ('info', U64 * 15)]
class ExceptionInfo(c.Structure):
    _fields_ = [('record', ExceptionRecord), ('first_chance', U32)]
class Event(c.Structure):
    _fields_ = [('code', U32), ('pid', U32), ('tid', U32), ('exception', ExceptionInfo)]
assert c.sizeof(Context) == 1232
for field, offset in [('flags',48),('dr0',72),('dr6',104),('dr7',112),('rax',120),
                      ('rsp',152),('r8',184),('r15',240),('rip',248)]:
    assert getattr(Context,field).offset == offset, (field,getattr(Context,field).offset)
assert c.sizeof(ExceptionRecord)==152 and c.sizeof(ExceptionInfo)==160
assert Event.exception.offset==16 and c.sizeof(Event)==176
for slot in range(4):
    original = (1 << (((slot+1)%4)*2)) | (9 << (16+slot*4))
    dr7 = (original & ~(15 << (16+slot*4))) | (13 << (16+slot*4)) | (1 << (slot*2))
    assert (dr7 >> (16+slot*4)) & 15 == 13
    mask = (3 << (slot*2)) | (15 << (16+slot*4))
    assert (dr7 & ~mask) | (original & mask) == original
print('AMD64 structural offsets and debug-register bit model passed; no Windows APIs exercised.')
