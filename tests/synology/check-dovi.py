#!/usr/bin/env python3
"""Check the Linux native ABI, glibc floor, and upstream FEL/MEL conversion vectors."""
import ctypes as c
from pathlib import Path
import re
import struct
import sys

library, fixtures = map(Path, sys.argv[1:])
binary = library.read_bytes()
assert binary[:5] == b"\x7fELF\x02" and struct.unpack_from("<H", binary, 18)[0] == 62
versions = {tuple(map(int, v.split(b"."))) for v in re.findall(rb"GLIBC_([0-9.]+)\x00", binary)}
assert versions and max(versions) <= (2, 17), versions
native = c.CDLL(str(library.resolve()))

def bind(name, result, *args):
    function = getattr(native, name)
    function.restype, function.argtypes = result, args
    return function

class Data(c.Structure):
    _fields_ = [("pointer", c.c_void_p), ("length", c.c_size_t)]

parse = bind("dovi_parse_unspec62_nalu", c.c_void_p, c.c_void_p, c.c_size_t)
error = bind("dovi_rpu_get_error", c.c_char_p, c.c_void_p)
header = bind("dovi_rpu_get_header", c.c_void_p, c.c_void_p)
convert = bind("dovi_convert_rpu_with_mode", c.c_int, c.c_void_p, c.c_uint8)
write = bind("dovi_write_unspec62_nalu", c.POINTER(Data), c.c_void_p)
free_rpu = bind("dovi_rpu_free", None, c.c_void_p)
free_header = bind("dovi_rpu_free_header", None, c.c_void_p)
free_data = bind("dovi_data_free", None, c.POINTER(Data))

def nals(path):
    return [nal for nal in re.split(b"\x00\x00\x00\x01", path.read_bytes()) if nal]

for kind, modes in (("fel", (1, 2)), ("mel", (1, 2)), ("fel", (2,)), ("mel", (2,))):
    before, expected = nals(fixtures / f"{kind}_orig.bin"), nals(fixtures / f"{kind}_to_81.bin")
    assert len(before) == len(expected) and before
    for nal, reference in zip(before, expected):
        buffer = c.create_string_buffer(nal)
        rpu = parse(buffer, len(nal))
        assert rpu
        try:
            assert error(rpu) is None
            h = header(rpu)
            assert h
            try:
                assert c.c_uint8.from_address(h).value == 7
            finally:
                free_header(h)
            for mode in modes:
                assert convert(rpu, mode) == 0 and error(rpu) is None
            data = write(rpu)
            assert data
            try:
                encoded = c.string_at(data.contents.pointer, data.contents.length)
                assert encoded[:2] == b"\x7c\x01"
                # Upstream's byte-exact vectors convert via MEL first. The
                # production path uses mode 2 directly; verify that output's
                # round-trip and resulting profile separately.
                if modes == (1, 2):
                    assert encoded[2:] == reference
                buffer2 = c.create_string_buffer(encoded)
                converted = parse(buffer2, len(encoded))
                assert converted and error(converted) is None
                try:
                    h2 = header(converted)
                    assert h2
                    try:
                        assert c.c_uint8.from_address(h2).value == 8
                    finally:
                        free_header(h2)
                finally:
                    free_rpu(converted)
            finally:
                free_data(data)
        finally:
            free_rpu(rpu)
    print(f"PASS {kind.upper()} conversion modes {modes}; P8.1 round-trip ({len(before)} RPUs)" + ("; matches upstream vectors" if modes == (1, 2) else ""))
print("PASS Linux x86_64 C ABI; maximum glibc symbol:", ".".join(map(str, max(versions))))
